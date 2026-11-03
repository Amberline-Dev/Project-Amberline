using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Amberline.Data;

namespace Amberline.Tools;

/// <summary>A small, deliberately offline Lua 5.1 register machine.
/// It is kept in the tools project because .luaobj scripts are research inputs,
/// not a runtime scripting surface for a match.</summary>
internal sealed class LuaVmException : Exception
{
    public LuaVmException(string message) : base(message) { }
}

internal sealed class LuaTable
{
    private readonly Dictionary<object, object?> values = new();
    private readonly List<object> order = new();
    public IEnumerable<KeyValuePair<object, object?>> Entries => order.Select(k => new KeyValuePair<object, object?>(k, values[k]));
    // Lua: reading t[nil] gives nil, writing t[nil] is an error.
    public object? Get(object? key) => key is not null && values.TryGetValue(Key(key), out var value) ? value : null;
    public void Set(object? key, object? value)
    {
        if (key is null) throw new LuaVmException("table index is nil");
        key = Key(key);
        if (!values.ContainsKey(key)) order.Add(key);
        values[key] = value;
    }
    public static object Key(object key) => key is int i ? (double)i : key;
}

internal sealed class LuaCell
{
    private object? value;
    public Action<object?>? Changed { get; set; }
    public object? Value { get => value; set { this.value = value; Changed?.Invoke(value); } }
    public LuaCell(object? value) => this.value = value;
}

internal sealed class LuaClosure
{
    public Proto Proto { get; }
    public LuaCell[] Upvalues { get; }
    public string CodeHash => Convert.ToHexString(SHA256.HashData(Proto.Code.SelectMany(BitConverter.GetBytes).ToArray())).ToLowerInvariant();
    public LuaClosure(Proto proto, LuaCell[] upvalues) { Proto = proto; Upvalues = upvalues; }
}

internal sealed class LuaStub
{
    public string Name { get; }
    public Func<object?[], object?[]> Invoke { get; }
    public Action<object?[]>? Observe { get; }
    public LuaStub(string name, Func<object?[], object?[]> invoke, Action<object?[]>? observe = null) { Name = name; Invoke = invoke; Observe = observe; }
    public object?[] Call(object?[] args) { Observe?.Invoke(args); return Invoke(args); }
}

internal sealed record Proto(
    IReadOnlyList<uint> Code,
    IReadOnlyList<object?> Constants,
    IReadOnlyList<Proto> Prototypes,
    int Upvalues,
    int Parameters,
    bool Vararg,
    int MaxStack);

internal sealed class LuaVm
{
    private readonly Dictionary<string, object?> globals = new(StringComparer.Ordinal);
    private object?[]? lastMulti;
    private LuaVm() { }
    public IReadOnlyDictionary<string, object?> Globals => globals;
    public void SetGlobal(string name, object? value) => globals[name] = value;
    public object? GetGlobal(string name) => globals.TryGetValue(name, out var value) ? value : null;
    public object?[] CallGlobal(string name, object?[] args) { try { return Call(globals.TryGetValue(name, out var value) ? value : null, args); } catch (LuaVmException ex) { throw new LuaVmException($"{name}: {ex.Message}"); } }
    public object?[] CallTableEntry(string name, object key, object?[] args) { try { return Call(Index(globals.TryGetValue(name, out var value) ? value : null, key), args); } catch (LuaVmException ex) { throw new LuaVmException($"{name}[{key}]: {ex.Message}"); } }

    public static LuaVm FromCase(JsonElement globalsElement, Func<string, LuaStub>? stubResolver = null)
    {
        var vm = new LuaVm();
        foreach (var property in globalsElement.EnumerateObject()) vm.globals[property.Name] = vm.ReadJson(property.Value, stubResolver);
        return vm;
    }

    public object?[] Run(Proto proto, object?[] args) => Call(new LuaClosure(proto, Array.Empty<LuaCell>()), args);

    private object?[] Call(object? target, object?[] args)
    {
        if (target is LuaStub stub) return stub.Call(args);
        if (target is not LuaClosure closure) throw new LuaVmException(target is null ? "call to a nil value" : $"attempt to call a non-function value: {Format(target)}");
        return Execute(closure, args);
    }

    private object?[] Execute(LuaClosure closure, object?[] args)
    {
        var proto = closure.Proto;
        var r = new object?[Math.Max(300, proto.MaxStack + 2)];
        for (var i = 0; i < proto.Parameters; i++) r[i] = i < args.Length ? args[i] : null;
        var varargs = proto.Vararg ? args.Skip(proto.Parameters).ToArray() : Array.Empty<object?>();
        var pc = 0;
        lastMulti = null;
        object? Rk(int x) => (x & 0x100) != 0 ? Constant(proto.Constants, x & 0xff) : r[x];
        while (pc < proto.Code.Count)
        {
            var ins = LuaInstruction.Decode(proto.Code[pc]);
            var op = ins.Opcode; var a = ins.A; var b = ins.B; var c = ins.C;
            switch (op)
            {
                case 0: r[a] = r[b]; break;
                case 1: r[a] = Constant(proto.Constants, ins.Bx); break;
                case 2: r[a] = b != 0; if (c != 0) pc++; break;
                case 3: for (var i = a; i <= b; i++) r[i] = null; break;
                case 4: r[a] = closure.Upvalues[b].Value; break;
                case 5: r[a] = globals.TryGetValue(StringConstant(proto.Constants, ins.Bx), out var g) ? g : null; break;
                case 6: r[a] = Index(r[b], Rk(c)); break;
                case 7: globals[StringConstant(proto.Constants, ins.Bx)] = r[a]; break;
                case 8: closure.Upvalues[b].Value = r[a]; break;
                case 9: SetIndex(r[a], Rk(b), Rk(c)); break;
                case 10: r[a] = new LuaTable(); break;
                case 11: r[a + 1] = r[b]; r[a] = Index(r[b], Rk(c)); break;
                case 12: r[a] = Number(Rk(b)) + Number(Rk(c)); break;
                case 13: r[a] = Number(Rk(b)) - Number(Rk(c)); break;
                case 14: r[a] = Number(Rk(b)) * Number(Rk(c)); break;
                case 15: r[a] = Number(Rk(b)) / Number(Rk(c)); break;
                case 16: { var x = Number(Rk(b)); var y = Number(Rk(c)); r[a] = y == 0 ? double.NaN : x - Math.Floor(x / y) * y; break; }
                case 17: r[a] = Math.Pow(Number(Rk(b)), Number(Rk(c))); break;
                case 18: r[a] = -Number(r[b]); break;
                case 19: r[a] = !Truthy(r[b]); break;
                case 20: r[a] = Length(r[b]); break;
                case 21: { var parts = new List<string>(); for (var i = b; i <= c; i++) parts.Add(ToStringValue(r[i])); r[a] = string.Concat(parts); break; }
                case 22: pc += ins.SBx; break;
                case 23: if ((EqualsLua(Rk(b), Rk(c))) != (a != 0)) pc++; break;
                case 24: if (Compare(Rk(b), Rk(c), false) != (a != 0)) pc++; break;
                case 25: if (Compare(Rk(b), Rk(c), true) != (a != 0)) pc++; break;
                case 26: if (Truthy(r[a]) != (c != 0)) pc++; break;
                case 27: if (Truthy(r[b]) == (c != 0)) r[a] = r[b]; else pc++; break;
                case 28:
                case 29:
                {
                    var callArgs = b == 0 ? (lastMulti ?? Array.Empty<object?>()) : r[(a + 1)..(a + b)];
                    object?[] result;
                    try { result = Call(r[a], callArgs); }
                    catch (LuaVmException ex) { throw new LuaVmException($"{ex.Message} at pc={pc} A={a}"); }
                    if (c == 0) { lastMulti = result; for (var i = 0; i < result.Length; i++) r[a + i] = result[i]; }
                    else for (var i = 0; i < c - 1; i++) r[a + i] = i < result.Length ? result[i] : null;
                    if (op == 29) return result;
                    break;
                }
                case 30: return b == 0 ? (lastMulti ?? Array.Empty<object?>()) : r[a..Math.Min(r.Length, a + b - 1)];
                case 31:
                    r[a] = Number(r[a]) + Number(r[a + 2]);
                    if (Number(r[a + 2]) > 0 ? Number(r[a]) <= Number(r[a + 1]) : Number(r[a]) >= Number(r[a + 1])) { r[a + 3] = r[a]; pc += ins.SBx; }
                    break;
                case 32: r[a] = Number(r[a]) - Number(r[a + 2]); pc += ins.SBx; break;
                case 33:
                {
                    object?[] result;
                    try { result = Call(r[a], new[] { r[a + 1], r[a + 2] }); }
                    catch (LuaVmException ex) { throw new LuaVmException($"{ex.Message} at pc={pc} TFORLOOP A={a}"); }
                    for (var i = 0; i < c; i++) r[a + 3 + i] = i < result.Length ? result[i] : null;
                    if (r[a + 3] is null) pc++; else r[a + 2] = r[a + 3];
                    break;
                }
                case 34:
                    if (r[a] is not LuaTable table) throw new LuaVmException("SETLIST target is not a table");
                    var list = b == 0 ? (lastMulti ?? Array.Empty<object?>()) : r[(a + 1)..(a + b + 1)];
                    var baseIndex = (c - 1) * 50;
                    for (var i = 0; i < list.Length; i++) table.Set((double)(baseIndex + i + 1), list[i]);
                    break;
                case 35: break;
                case 36:
                    if ((uint)ins.Bx >= (uint)proto.Prototypes.Count) throw new LuaVmException($"CLOSURE references missing prototype {ins.Bx}");
                    var child = proto.Prototypes[ins.Bx]; var cells = new LuaCell[child.Upvalues];
                    for (var i = 0; i < cells.Length; i++)
                    {
                        pc++; var capture = LuaInstruction.Decode(proto.Code[pc]);
                        if (capture.Opcode == 0)
                        {
                            var register = capture.B; var cell = new LuaCell(r[register]); cell.Changed = value => r[register] = value; cells[i] = cell;
                        }
                        else if (capture.Opcode == 4) cells[i] = closure.Upvalues[capture.B];
                        else throw new LuaVmException("bad CLOSURE upvalue pseudo-op");
                    }
                    r[a] = new LuaClosure(child, cells); break;
                case 37:
                    if (b == 0) { lastMulti = varargs; for (var i = 0; i < varargs.Length; i++) r[a + i] = varargs[i]; }
                    else for (var i = 0; i < b - 1; i++) r[a + i] = i < varargs.Length ? varargs[i] : null;
                    break;
                default: throw new LuaVmException($"unsupported opcode {op} at pc={pc}");
            }
            pc++;
        }
        return Array.Empty<object?>();
    }

    private object? ReadJson(JsonElement value, Func<string, LuaStub>? stubResolver)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False) return value.GetBoolean();
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("stub", out var stub))
            {
                if (stub.ValueKind == JsonValueKind.String)
                {
                    var name = stub.GetString() ?? throw new LuaVmException("Lua stub name must be a string");
                    return stubResolver?.Invoke(name) ?? throw new LuaVmException($"implicit Lua stub '{name}' is unavailable; provide an explicit binding");
                }
                if (stub.ValueKind != JsonValueKind.Object)
                    throw new LuaVmException("Lua stub must be a name or an explicit binding object");
                var bindingName = stub.TryGetProperty("name", out var nameElement) &&
                    nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()!
                    : throw new LuaVmException("explicit Lua stub binding requires a string name");
                var returns = stub.TryGetProperty("returns", out var returnsElement)
                    ? returnsElement.ValueKind == JsonValueKind.Array
                        ? returnsElement.EnumerateArray().Select(item => ReadJson(item, stubResolver)).ToArray()
                        : throw new LuaVmException("explicit Lua stub returns must be an array")
                    : Array.Empty<object?>();
                return new LuaStub(bindingName, _ => returns.ToArray());
            }
            var table = new LuaTable();
            if (value.TryGetProperty("table", out var entries)) foreach (var entry in entries.EnumerateArray()) table.Set(ReadJson(entry[0], stubResolver) ?? throw new LuaVmException("nil table key"), ReadJson(entry[1], stubResolver));
            return table;
        }
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().Select(item => ReadJson(item, stubResolver)).ToArray();
        return null;
    }
    public static LuaStub CreateStub(string name, Func<object?[], object?[]> invoke, Action<object?[]>? observe = null) => new(name, invoke, observe);

    private static object? Constant(IReadOnlyList<object?> constants, int index) => (uint)index < (uint)constants.Count ? constants[index] : throw new LuaVmException($"constant index {index} is out of range");
    private static string StringConstant(IReadOnlyList<object?> constants, int index) => Constant(constants, index) as string ?? throw new LuaVmException($"constant {index} is not a string");
    private static object? Index(object? target, object? key) => target is LuaTable table ? table.Get(key!) : throw new LuaVmException($"attempt to index a {TypeName(target)} value");
    private static void SetIndex(object? target, object? key, object? value) { if (target is not LuaTable table) throw new LuaVmException($"attempt to index a {TypeName(target)} value"); table.Set(key!, value); }
    private static double Number(object? value) => value is double d ? d : throw new LuaVmException(value is string s ? $"attempt to perform arithmetic/compare on '{s}'" : $"attempt to perform arithmetic/compare on a {TypeName(value)} value");
    private static bool Truthy(object? value) => value is not null && value is not false;
    private static object Length(object? value) => value is string s ? (double)s.Length : value is LuaTable t ? (double)t.Entries.Count() : throw new LuaVmException($"attempt to get length of a {TypeName(value)} value");
    private static string ToStringValue(object? value) => value is string s ? s : value is double d ? (d % 1 == 0 ? d.ToString("0", CultureInfo.InvariantCulture) : d.ToString("G17", CultureInfo.InvariantCulture)) : throw new LuaVmException($"attempt to concatenate a {TypeName(value)} value");
    /// <summary>Lua orders two strings with &lt; and &lt;= (byte order, the C locale's strcoll). Off by default: the conformance corpus pins the strict behaviour.</summary>
    public bool OrderStrings { get; set; }

    private bool Compare(object? a, object? b, bool orEqual)
    {
        if (OrderStrings && a is string x && b is string y) { var order = string.CompareOrdinal(x, y); return orEqual ? order <= 0 : order < 0; }
        var left = Number(a); var right = Number(b);
        return orEqual ? left <= right : left < right;
    }
    private static bool EqualsLua(object? a, object? b) => a is double da && b is double db ? da == db : Equals(a, b);
    private static string TypeName(object? value) => value switch { null => "nil", bool => "boolean", double => "number", string => "string", LuaTable => "table", LuaClosure or LuaStub => "function", _ => value.GetType().Name };
    private static string Format(object? value) => value switch { null => "nil", double d => d % 1 == 0 ? d.ToString("0.0", CultureInfo.InvariantCulture) : d.ToString(CultureInfo.InvariantCulture), string s => $"'{s}'", LuaTable table => $"table[{string.Join(',', table.Entries.Take(3).Select(x => x.Key))}]", _ => value.ToString() ?? "?" };

    public static Proto ReadProto(JsonElement e) => new(
        e.GetProperty("code").EnumerateArray().Select(x => x.GetUInt32()).ToArray(),
        e.GetProperty("constants").EnumerateArray().Select(ReadConstant).ToArray(),
        e.GetProperty("prototypes").EnumerateArray().Select(ReadProto).ToArray(),
        e.GetProperty("upvalueCount").GetInt32(), e.GetProperty("parameterCount").GetInt32(), e.GetProperty("isVararg").GetInt32() != 0, e.GetProperty("maxStackSize").GetInt32());

    public static Proto ReadProto(LuaPrototype p) => new(
        p.Code, p.Constants.Select(x => x switch { LuaConstant.Nil => null, LuaConstant.Boolean b => (object?)b.Value, LuaConstant.Number n => n.Value, LuaConstant.String s => s.Value, _ => null }).ToArray(),
        p.Prototypes.Select(ReadProto).ToArray(), p.UpvalueCount, p.ParameterCount, p.IsVararg != 0, p.MaxStackSize);

    private static object? ReadConstant(JsonElement e) => e.ValueKind switch { JsonValueKind.Null => null, JsonValueKind.String => e.GetString(), JsonValueKind.Number => e.GetDouble(), JsonValueKind.True or JsonValueKind.False => e.GetBoolean(), _ => throw new LuaVmException("unsupported constant") };

    public static object? Normalize(object? value) => value switch
    {
        null => null,
        LuaStub stub => new Dictionary<string, object?> { ["stub"] = stub.Name },
        IReadOnlyDictionary<string, object?> map => map.ToDictionary(x => x.Key, x => Normalize(x.Value)),
        IDictionary<string, object?> map => map.ToDictionary(x => x.Key, x => Normalize(x.Value)),
        LuaTable table => new Dictionary<string, object?> { ["table"] = table.Entries.Select(x => (object?)new object?[] { Normalize(x.Key), Normalize(x.Value) }).ToArray() },
        LuaClosure closure => new Dictionary<string, object?> { ["closure"] = true, ["functionCodeHash"] = closure.CodeHash },
        object?[] array => array.Select(Normalize).ToArray(),
        _ => value
    };
}
