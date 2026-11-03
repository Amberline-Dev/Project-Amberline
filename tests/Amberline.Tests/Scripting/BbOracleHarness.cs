using System.Text;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Tools;

namespace Amberline.Tests;

// Test-only differential oracle for the BuildingBlocks interpreter. The same shipped blocks run twice against one
// deterministic stub world: once through the production BbInterpreter, once through the repo's offline Lua VM
// (Tools/LuaVm.cs) executing the shipped DATA/BuildingBlocks/BuildingBlocksBase.luaobj bytecode. Every call that crosses
// the host boundary is appended to a log by the shared OracleCore, so the two runs can be compared call for call
// (function, arguments in order, native param dumps and the scope at each native call) and by their final scope.
// The Lua VM never ships: it lives in Tools and is used here only as a test oracle.

/// <summary>A stub unit; equality is by value, so the same handle is the same unit on both sides.</summary>
internal sealed record OracleUnit(string Name, double Team, double Level, int Id)
{
    public override string ToString() => Name;
}

/// <summary>A position result, turned into a table by each side.</summary>
internal sealed record OraclePoint(double X, double Y, double Z);

/// <summary>A table argument, already rendered, so both sides pass the same text to the core.</summary>
internal sealed record OracleSnap(string Text);

internal sealed class OracleCore
{
    public static readonly OracleUnit Nothing = new("Nothing", 0, 0, 0);
    public static readonly OracleUnit Owner = new("Owner", 100, 5, 1);
    public static readonly OracleUnit Attacker = new("Attacker", 200, 7, 2);
    public static readonly OracleUnit Target = new("Target", 200, 4, 3);
    public static readonly OracleUnit Caster = new("Caster", 100, 6, 4);
    public static readonly OracleUnit Unit = new("Unit", 200, 3, 5);
    public static readonly OracleUnit Other1 = new("Other1", 100, 2, 6);
    public static readonly OracleUnit Other2 = new("Other2", 200, 8, 7);
    public static readonly OracleUnit Other3 = new("Other3", 300, 1, 8);
    public static readonly OracleUnit[] Iterated = [new("Iter1", 200, 2, 9), new("Iter2", 100, 9, 10)];

    private int _clock;
    private int _randoms;
    private int _sources;

    public List<string> Log { get; } = new();

    /// <summary>Records one host call and returns the stub world's answer. Trailing nil arguments are dropped: an omitted argument and a nil one look alike to the stub.</summary>
    public object? Call(string function, params object?[] args)
    {
        var count = args.Length;
        while (count > 0 && args[count - 1] is null) count--;
        var shown = string.Join(", ", args.Take(count).Select(Canon.Value));
        Log.Add($"{function}({shown})");
        return Answer(function, args.Take(count).ToArray(), Fnv(function + "|" + shown));
    }

    /// <summary>
    /// Params of natives that name a variable the native fills in. The stub writes a number there when it is still unset, on
    /// both sides, so later blocks that read the result run instead of failing on nil (an input var is already set).
    /// </summary>
    public static readonly string[] OutputKeys =
        ["DestVar", "ResultVar", "PositionVar", "SecondsVar", "SkinIDVar", "OutputAngleVar", "BubbleIDVar", "EffectIDVar", "MissileIDVar", "IDVar"];

    public const double OutputValue = 2.0;

    public void Mark(string text) => Log.Add(text);

    public void Native(string function, string parameters, string scope) => Log.Add($"N {function} [{parameters}] scope={scope}");

    private object? Answer(string fn, object?[] a, uint h)
    {
        bool Flip(uint mod = 2) => h % mod == 0;
        double Spread(uint mod, double add = 0) => add + h % mod;
        switch (fn)
        {
            case "GetTeamID": return a.Length > 0 && a[0] is OracleUnit unit ? unit.Team : 100.0;
            case "IsObjectAI": case "IsObjectHero": case "IsMelee": case "IsTurretAI": return Flip();
            case "IsDead": return Flip(4);
            case "GetSourceType": return 5.0 + _sources++ % 5;
            case "IsTargetInFrontOfMe": case "IsTargetBehindMe": case "HasBuffOfType": return Flip();
            case "SpellBuffCount": return Spread(4);
            case "HasPARType": return a.Length > 1 && a[1] is double par ? par == 0.0 : Flip();
            case "GetPosition": return a.Length > 0 && a[0] is OracleUnit at ? new OraclePoint(at.Id * 100, 0, at.Id * 50) : new OraclePoint(0, 0, 0);
            case "GetNormalizedPositionDelta": return new OraclePoint(0.6, 0, 0.8);
            case "Make3DPoint":
                return a.Length >= 3 && a[0] is double x && a[1] is double y && a[2] is double z ? new OraclePoint(x, y, z) : new OraclePoint(0, 0, 0);
            case "GetTime": return 10.0 + 0.5 * _clock++;
            case "Random": return (_randoms++ * 0.37 + 0.11) % 1.0;
            case "GetLevel": return a.Length > 0 && a[0] is OracleUnit lv ? lv.Level : 5.0;
            case "GetArmor": case "GetSpellBlock": case "GetTotalAttackDamage": return Spread(40, 20);
            case "DistanceBetweenObjects": case "DistanceBetweenObjectAndPoint": case "DistanceBetweenPoints": return Spread(500, 100);
            case "GetUnitSkinName": return "Skin" + (a.Length > 0 ? Canon.Value(a[0]) : "");
            case "GetGold": return 500.0;
            case "GetCastSpellLevelPlusOne": return 3.0;
            case "GetCastSpellTargetsHitPlusOne": return 2.0;
            case "GetSpellSlot": return 1.0;
            case "GetPARCost": return 60.0;
            case "GetSpellName": return "OracleSpell";
            case "GetIsAttackOverride": return false;
            case "GetCasterID": return 4.0;
            case "GetCastSpellTargetPos": return new OraclePoint(10, 0, 20);
            case "GetSlotSpellLevel": return 1.0 + h % 5;
            case "GetSlotSpellName": return "Spell" + h % 7;
            case "GetSlotSpellCooldownTime": return (h % 20) * 0.5;
        }
        if (BbNames<BbGetStatus>.Contains(fn)) return Flip();
        if (BbNames<BbGetStat>.Contains(fn)) return Spread(100, 1);
        if (BbNames<BbGetResource>.Contains(fn)) return Spread(900, 100);
        return null; // Inc*, Set*, SpellBuffAdd, Say, ... have no answer
    }

    private static uint Fnv(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text) hash = (hash ^ c) * 16777619u;
        return hash;
    }
}

/// <summary>Canonical text for runtime values of both engines, so equal state renders as equal strings.</summary>
internal static class Canon
{
    public static string Value(object? value, int depth = 0)
    {
        switch (value)
        {
            case null: return "nil";
            case bool b: return b ? "true" : "false";
            case double d: return BbNumber(d);
            case string s: return "\"" + s + "\"";
            case OracleUnit u: return u.Name;
            case OraclePoint p: return $"({BbNumber(p.X)},{BbNumber(p.Y)},{BbNumber(p.Z)})";
            case OracleSnap snap: return snap.Text;
            case BbFunctionRef f: return BbBaseLibrary.Contains(f.Name) ? "fn" : f.Name;
            case LuaStub stub: return stub.Name;
            case LuaClosure: case BbValue.Closure: return "fn";
            case BbTable table: return Table(table.Entries.Select(e => (e.Key, e.Value)), depth);
            case LuaTable lua: return Table(lua.Entries.Select(e => (e.Key, e.Value)), depth);
            default: return value.GetType().Name;
        }
    }

    private static string Table(IEnumerable<(object Key, object? Value)> entries, int depth)
    {
        if (depth > 4) return "{...}";
        var live = entries.Where(e => e.Value is not null).ToList();
        var ordered = live.OrderBy(e => e.Key is double ? 0 : 1).ThenBy(e => e.Key is double d ? d : 0).ThenBy(e => e.Key.ToString(), StringComparer.Ordinal);
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var (key, value) in ordered)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(key is double k ? $"[{BbNumber(k)}]" : key.ToString()).Append('=').Append(Value(value, depth + 1));
        }
        return sb.Append('}').ToString();
    }

    private static string BbNumber(double d) => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The resolved params of a block as the native sees them (nil values left out, like a Lua table): one text for both engines.</summary>
    public static string Params(IEnumerable<BbField> fields)
    {
        var parts = new List<string>();
        foreach (var field in fields)
            if (BbRuntime.ToRuntime(field.Value) is { } value) parts.Add(field.Key + "=" + Value(value));
        return string.Join(", ", parts);
    }
}

/// <summary>The production-side stub host: every interface method forwards to the shared core.</summary>
internal sealed class OracleHost : IBbHost, IBbNativeHandler
{
    private readonly OracleCore _core;

    public OracleHost(OracleCore core) => _core = core;

    private static object? A(object? v) => v switch
    {
        BbTable t => new OracleSnap(Canon.Value(t)),
        BbFunctionRef f => f.Name,
        _ => v,
    };

    private object? Call(string function, params object?[] args) => _core.Call(function, args.Select(A).ToArray());

    private static object? R(object? v) => v is OraclePoint p ? BbTable.Point(p.X, p.Y, p.Z) : v;

    public IBbNativeHandler? NativeHandler(BbNativeOwner owner) => this;

    // IBbUnitHost
    public double GetTeamID(object? unit) => (double)Call("GetTeamID", unit)!;
    public bool IsObjectAI(object? unit) => (bool)Call("IsObjectAI", unit)!;
    public bool IsObjectHero(object? unit) => (bool)Call("IsObjectHero", unit)!;
    public bool IsMelee(object? unit) => (bool)Call("IsMelee", unit)!;
    public bool IsTurretAI(object? unit) => (bool)Call("IsTurretAI", unit)!;
    public bool IsDead(object? unit) => (bool)Call("IsDead", unit)!;
    public string? GetUnitSkinName(object? unit) => (string?)Call("GetUnitSkinName", unit);
    public void SetScaleSkinCoef(object? scale, object? unit) => Call("SetScaleSkinCoef", scale, unit);

    // IBbGeometryHost
    public object? GetPosition(object? unit) => R(Call("GetPosition", unit));
    public object? GetNormalizedPositionDelta(object? target, object? attacker, bool flag) => R(Call("GetNormalizedPositionDelta", target, attacker, flag));
    public void SetPosition(object? unit, object? position) => Call("SetPosition", unit, position);
    public object? Make3DPoint(object? x, object? y, object? z) => R(Call("Make3DPoint", x, y, z));
    public double DistanceBetweenObjects(object? a, object? b) => (double)Call("DistanceBetweenObjects", a, b)!;
    public double DistanceBetweenObjectAndPoint(object? unit, object? point) => (double)Call("DistanceBetweenObjectAndPoint", unit, point)!;
    public double DistanceBetweenPoints(object? a, object? b) => (double)Call("DistanceBetweenPoints", a, b)!;
    public bool IsTargetInFrontOfMe(object? me, object? target) => (bool)Call("IsTargetInFrontOfMe", me, target)!;
    public bool IsTargetBehindMe(object? me, object? target) => (bool)Call("IsTargetBehindMe", me, target)!;

    // IBbBuffHost
    public bool HasBuffOfType(object? unit, double? buffType) => (bool)Call("HasBuffOfType", unit, buffType)!;
    public double SpellBuffCount(object? unit, string? buffName, object? caster) => (double)Call("SpellBuffCount", unit, buffName, caster)!;
    public void SpellBuffAdd(object? target, object? attacker, string buffName, double addType, double maxStack, double duration, BbTable? vars) =>
        Call("SpellBuffAdd", target, attacker, buffName, addType, maxStack, duration, vars);
    public void SpellBuffRemove(object? target, string? buffName, object? attacker) => Call("SpellBuffRemove", target, buffName, attacker);

    // IBbCombatHost
    public double GetSourceType() => (double)Call("GetSourceType")!;

    // IBbStatHost
    public double GetLevel(object? unit) => (double)Call("GetLevel", unit)!;
    public double GetArmor(object? unit) => (double)Call("GetArmor", unit)!;
    public double GetSpellBlock(object? unit) => (double)Call("GetSpellBlock", unit)!;
    public double GetTotalAttackDamage(object? unit) => (double)Call("GetTotalAttackDamage", unit)!;
    public void IncStat(BbIncStat stat, double delta, object? unit) => Call(stat.ToString(), delta, unit);
    public double GetStat(BbGetStat stat, object? unit) => (double)Call(stat.ToString(), unit)!;
    public bool GetStatus(BbGetStatus status, object? unit) => (bool)Call(status.ToString(), unit)!;
    public void SetStatus(BbSetStatus status, object? unit, object? value) => Call(status.ToString(), unit, value);

    // IBbResourceHost
    public bool HasPARType(object? unit, double? parType) => (bool)Call("HasPARType", unit, parType)!;
    public object? GetResource(BbGetResource function, object? unit, double? parType) => Call(function.ToString(), unit, parType);
    public double GetGold(BbCall call) => (double)Call("GetGold")!;
    public void ReincarnateNonDeadHero(object? unit) => Call("ReincarnateNonDeadHero", unit);

    // IBbSpellHost
    public object? GetSlotSpellInfo(BbSlotSpellInfo function, object? owner, object? slot, double? spellbook, double? slotType) =>
        Call(function.ToString(), owner, slot, spellbook, slotType);
    public void SetSlotSpellCooldownTime(object? owner, object? slot, double? spellbook, double? slotType, object? cooldown) =>
        Call("SetSlotSpellCooldownTime", owner, slot, spellbook, slotType, cooldown);
    public void SetSpellCastRange(double? range) => Call("SetSpellCastRange", range);
    public void SetCanCastWhileDisabled(bool canCast) => Call("SetCanCastWhileDisabled", canCast);
    public object? GetCastInfo(BbCastInfo info, object? unit) => R(Call(info.ToString(), unit));

    // IBbGameHost
    public double GetTime() => (double)Call("GetTime")!;
    public double Random() => (double)Call("Random")!;
    public void Say(object? unit, string? text) => Call("Say", unit, text);
    public void Alert(string? text) => Call("Alert", text);

    // IBbOrderHost
    public void IssueOrder(object? whom, double? order, object? position, object? target) => Call("IssueOrder", whom, order, position, target);
    public void DefUpdateAura(object? center, double? range, double? unitScan, string? buffName) => Call("DefUpdateAura", center, range, unitScan, buffName);

    // IBbNativeHandler: record the call, then run the sub-blocks the way an iterating native would.
    public void Execute(BbCall call)
    {
        _core.Native(call.Function, Canon.Params(call.Params), Canon.Value(call.Scope.Vars));
        foreach (var key in OracleCore.OutputKeys)
        {
            if (call.Value(key) is not string outputName) continue;
            var table = call.Table(key + "Table", true);
            if (table[outputName] is null) table[outputName] = OracleCore.OutputValue;
        }
        if (call.SubBlocks.Count == 0) return;
        var iterator = call.Param("IteratorVar") is BbValue.Text name ? name.Value : null;
        foreach (var unit in OracleCore.Iterated)
        {
            if (iterator is not null) call.Scope.Vars[iterator] = unit;
            call.ExecuteSubBlocks();
        }
    }
}

/// <summary>
/// Variables a hook chain would normally find set by earlier hooks or natives: every variable name a block reads is
/// pre-set to a small deterministic number in the table the block names (or the root), so the replay does not stop at the
/// first nil. Unit slot names are left alone.
/// </summary>
internal static class OracleSeeds
{
    private static readonly HashSet<string> UnitSlots = ["Owner", "Attacker", "Target", "Caster", "Unit", "Nothing", "Other1", "Other2", "Other3"];

    public static IReadOnlyList<(string? Table, string Name, double Value)> For(IReadOnlyList<(string Hook, IReadOnlyList<BbBlock> Blocks)> plan)
    {
        var seeds = new Dictionary<(string?, string), double>();
        void Visit(BbBlock block)
        {
            foreach (var field in block.Params)
            {
                if (!field.Key.EndsWith("Var", StringComparison.Ordinal) || field.Value is not BbValue.Text text) continue;
                if (field.Key.StartsWith("Dest", StringComparison.Ordinal) || field.Key.StartsWith("Iterator", StringComparison.Ordinal) || UnitSlots.Contains(text.Value)) continue;
                var table = block.Param(field.Key + "Table") is BbValue.Text named ? named.Value : null;
                seeds.TryAdd((table, text.Value), 2.0 + text.Value.Sum(c => c) % 5);
            }
            foreach (var child in block.SubBlocks) Visit(child);
        }
        foreach (var (_, blocks) in plan)
            foreach (var block in blocks) Visit(block);
        return seeds.Select(e => (e.Key.Item1, e.Key.Item2, e.Value)).ToList();
    }
}

internal sealed record OracleOutcome(IReadOnlyList<string> Log, string Scope, string Globals, int Errors, IReadOnlyList<string> ErrorTexts);

/// <summary>The Lua side: BuildingBlocksBase.luaobj on the offline VM with stub host functions that log through the core.</summary>
internal sealed class OracleLua
{
    private readonly LuaVm _vm;
    private readonly OracleCore _core = new();

    // Host functions the base file calls by global name, and the core function each one maps to.
    private static readonly (string Lua, string Core)[] Primitives =
    [
        ("GetTeamID", "GetTeamID"), ("IsObjectAI", "IsObjectAI"), ("IsObjectHero", "IsObjectHero"), ("IsMelee", "IsMelee"),
        ("IsTurretAI", "IsTurretAI"), ("IsDead", "IsDead"), ("GetSourceType", "GetSourceType"),
        ("BBIsTargetInFrontOfMe", "IsTargetInFrontOfMe"), ("BBIsTargetBehindMe", "IsTargetBehindMe"),
        ("HasBuffOfType", "HasBuffOfType"), ("SpellBuffCount", "SpellBuffCount"), ("SpellBuffAdd", "SpellBuffAdd"),
        ("SpellBuffRemove", "SpellBuffRemove"), ("HasPARType", "HasPARType"), ("GetPosition", "GetPosition"),
        ("GetNormalizedPositionDelta", "GetNormalizedPositionDelta"), ("SetPosition", "SetPosition"), ("GetTime", "GetTime"),
        ("GetLevel", "GetLevel"), ("GetArmor", "GetArmor"), ("GetSpellBlock", "GetSpellBlock"),
        ("GetTotalAttackDamage", "GetTotalAttackDamage"), ("GetUnitSkinName", "GetUnitSkinName"),
        ("SetCanCastWhileDisabled", "SetCanCastWhileDisabled"), ("Say", "Say"), ("_ALERT", "Alert"),
        ("Make3DPoint", "Make3DPoint"), ("DefUpdateAura", "DefUpdateAura"), ("ReincarnateNonDeadHero", "ReincarnateNonDeadHero"),
        ("IssueOrder", "IssueOrder"), ("SetSpellCastRange", "SetSpellCastRange"),
        ("SetSlotSpellCooldownTime", "SetSlotSpellCooldownTime"), ("DistanceBetweenObjects", "DistanceBetweenObjects"),
        ("DistanceBetweenObjectAndPoint", "DistanceBetweenObjectAndPoint"), ("DistanceBetweenPoints", "DistanceBetweenPoints"),
        ("SetScaleSkinCoef", "SetScaleSkinCoef"),
    ];

    public OracleLua(GameDataPaths paths)
    {
        using var empty = System.Text.Json.JsonDocument.Parse("{}");
        _vm = LuaVm.FromCase(empty.RootElement);
        _vm.OrderStrings = true; // real Lua orders strings; the VM's conformance corpus pins the strict default
        InstallStandardLibrary();
        foreach (var (lua, core) in Primitives) _vm.SetGlobal(lua, Stub(lua, args => Call(core, args)));
        // Accessors BBIncStat & co. receive as parameter values: each enum name is a global function with the core's name.
        foreach (var name in AccessorNames()) _vm.SetGlobal(name, Stub(name, args => Call(name, args)));
        _vm.SetGlobal("BBLuaGetGold", Stub("BBLuaGetGold", _ => Call("GetGold", [])));
        _vm.SetGlobal("BBTeleportToPosition", Stub("BBTeleportToPosition", args => NativeFromTables("BBTeleportToPosition", args)));
        _vm.Run(LuaVm.ReadProto(LuaObjectReader.ParseData(paths, "BuildingBlocks/BuildingBlocksBase.luaobj").TopLevel), []);
    }

    public OracleCore Core => _core;

    /// <summary>Calls a global of the loaded base file (CO_LESS_THAN, GetTable, ...).</summary>
    public object?[] CallGlobal(string name, params object?[] args) => _vm.CallGlobal(name, args);

    public static IEnumerable<string> AccessorNames() =>
        Enum.GetNames<BbIncStat>().Concat(Enum.GetNames<BbSetStatus>()).Concat(Enum.GetNames<BbGetStatus>())
            .Concat(Enum.GetNames<BbGetStat>()).Concat(Enum.GetNames<BbGetResource>()).Concat(Enum.GetNames<BbSlotSpellInfo>())
            .Concat(Enum.GetNames<BbCastInfo>()).Distinct();

    private LuaStub Stub(string name, Func<object?[], object?[]> invoke) => LuaVm.CreateStub(name, invoke);

    private object?[] Call(string coreName, object?[] args)
    {
        var result = _core.Call(coreName, args.Select(FromLua).ToArray());
        return result is null ? [] : [ToLua(result)];
    }

    private static object? FromLua(object? v) => v switch
    {
        LuaTable t => new OracleSnap(Canon.Value(t)),
        _ => v,
    };

    private static object? ToLua(object? v)
    {
        if (v is not OraclePoint p) return v;
        var table = new LuaTable();
        table.Set("x", p.X);
        table.Set("y", p.Y);
        table.Set("z", p.Z);
        return table;
    }

    private void InstallStandardLibrary()
    {
        _vm.SetGlobal("type", Stub("type", a => [a.Length == 0 || a[0] is null ? "nil" : a[0] switch
        {
            bool => "boolean", double => "number", string => "string", LuaTable => "table",
            LuaClosure or LuaStub => "function", _ => "userdata",
        }]));
        _vm.SetGlobal("tostring", Stub("tostring", a => [BbNumberText(a.Length == 0 ? null : a[0])]));
        var strings = new LuaTable();
        strings.Set("lower", Stub("string.lower", a => [a[0] is string s ? new string(s.Select(c => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c).ToArray()) : throw new LuaVmException("bad argument to string.lower")]));
        _vm.SetGlobal("string", strings);
        var math = new LuaTable();
        double Arg(object?[] a) => a.Length > 0 && a[0] is double d ? d : throw new LuaVmException("bad argument to math function");
        math.Set("floor", Stub("math.floor", a => [Math.Floor(Arg(a))]));
        math.Set("ceil", Stub("math.ceil", a => [Math.Ceiling(Arg(a))]));
        math.Set("sin", Stub("math.sin", a => [Math.Sin(Arg(a))]));
        math.Set("cos", Stub("math.cos", a => [Math.Cos(Arg(a))]));
        math.Set("tan", Stub("math.tan", a => [Math.Tan(Arg(a))]));
        math.Set("rad", Stub("math.rad", a => [Arg(a) * (Math.PI / 180.0)]));
        math.Set("random", Stub("math.random", _ => Call("Random", [])));
        _vm.SetGlobal("math", math);
    }

    // tostring(): the interpreter's own formatting rules (Lua 5.1 %.14g), shared deliberately; the check is about block semantics.
    private static string BbNumberText(object? v) => v switch
    {
        null => "nil",
        bool b => b ? "true" : "false",
        double d => d.ToString("G14", System.Globalization.CultureInfo.InvariantCulture).Replace("E+", "e+").Replace("E-", "e-"),
        string s => s,
        LuaTable => "table",
        _ => "userdata",
    };

    private object?[] NativeFromTables(string function, object?[] args)
    {
        var scope = (LuaTable)args[0]!;
        _core.Native(function, ParamsOf((LuaTable)args[1]!), Canon.Value(scope));
        return [];
    }

    private static string ParamsOf(LuaTable table) =>
        string.Join(", ", table.Entries.Where(e => e.Value is not null).Select(e => e.Key + "=" + Canon.Value(e.Value)));

    // ---- shipped blocks as Lua tables ----

    private LuaTable Blocks(IReadOnlyList<BbBlock> blocks)
    {
        var table = new LuaTable();
        for (var i = 0; i < blocks.Count; i++) table.Set((double)(i + 1), Block(blocks[i]));
        return table;
    }

    private LuaTable Block(BbBlock block)
    {
        var table = new LuaTable();
        table.Set("Function", FunctionValue(block));
        var parameters = new LuaTable();
        foreach (var field in block.Params) parameters.Set(field.Key, Value(field.Value));
        table.Set("Params", parameters);
        table.Set("SubBlocks", Blocks(block.SubBlocks));
        return table;
    }

    private object? FunctionValue(BbBlock block)
    {
        if (_vm.GetGlobal(block.Function) is LuaClosure closure) return closure; // a Lua function of the base file
        // A native: a stub bound to this block, which logs exactly what the production handler logs.
        return Stub(block.Function, args =>
        {
            var scope = (LuaTable)args[0]!;
            var parameters = (LuaTable)args[1]!;
            _core.Native(block.Function, ParamsOf(parameters), Canon.Value(scope));
            foreach (var key in OracleCore.OutputKeys)
            {
                if (parameters.Get(key) is not string outputName) continue;
                var table = (LuaTable)_vm.CallGlobal("GetTable", [scope, parameters.Get(key + "Table"), true])[0]!;
                if (table.Get(outputName) is null) table.Set(outputName, OracleCore.OutputValue);
            }
            if (block.SubBlocks.Count == 0) return [];
            var iterator = block.Param("IteratorVar") is BbValue.Text name ? name.Value : null;
            foreach (var unit in OracleCore.Iterated)
            {
                if (iterator is not null) scope.Set(iterator, unit);
                _vm.CallGlobal("ExecuteBuildingBlocks", [args[2], scope]);
            }
            return [];
        });
    }

    private object? Value(BbValue value) => value switch
    {
        BbValue.Nil => null,
        BbValue.Bool b => b.Value,
        BbValue.Number n => n.Value,
        BbValue.Text t => t.Value,
        BbValue.Reference r => Reference(r.Name),
        BbValue.Table t => TableValue(t),
        _ => null,
    };

    private object? Reference(string name)
    {
        if (BbConstants.TryGetValue(name, out var number)) return number;
        return _vm.GetGlobal(name); // base-file closures (CO_*, MO_*) and the accessor stubs; an undefined global stays nil
    }

    private LuaTable TableValue(BbValue.Table source)
    {
        var table = new LuaTable();
        for (var i = 0; i < source.Items.Count; i++) table.Set((double)(i + 1), Value(source.Items[i]));
        foreach (var field in source.Fields) table.Set(field.Key, Value(field.Value));
        return table;
    }

    private static LuaTable NewScope(double level, Func<LuaTable> make)
    {
        var scope = make();
        scope.Set("Owner", OracleCore.Owner);
        scope.Set("Attacker", OracleCore.Attacker);
        scope.Set("Target", OracleCore.Target);
        scope.Set("Caster", OracleCore.Caster);
        scope.Set("Unit", OracleCore.Unit);
        scope.Set("Nothing", OracleCore.Nothing);
        scope.Set("Other1", OracleCore.Other1);
        scope.Set("Other2", OracleCore.Other2);
        scope.Set("Other3", OracleCore.Other3);
        foreach (var name in new[] { "InstanceVars", "NextBuffVars", "CharVars", "SpellVars", "AvatarVars" }) scope.Set(name, make());
        scope.Set("Level", level);
        return scope;
    }

    /// <summary>Runs the hooks in order against one shared scope, as the Lua base file would.</summary>
    public OracleOutcome Run(IReadOnlyList<(string Hook, IReadOnlyList<BbBlock> Blocks)> plan, double level,
        IReadOnlyList<(string? Table, string Name, double Value)>? seeds = null)
    {
        var scope = NewScope(level, () => new LuaTable());
        foreach (var (table, name, value) in seeds ?? [])
        {
            var target = table is not null && scope.Get(table) is LuaTable named ? named : scope;
            target.Set(name, value);
        }
        var errors = 0;
        var texts = new List<string>();
        foreach (var (hook, blocks) in plan)
        {
            _core.Mark("== " + hook);
            try { _vm.CallGlobal("ExecuteBuildingBlocks", [Blocks(blocks), scope]); }
            catch (Exception ex) when (ex is LuaVmException or InvalidCastException or NullReferenceException)
            {
                errors++;
                texts.Add(ex.Message);
                _core.Mark("!! error");
            }
        }
        var globals = $"{Canon.Value(_vm.GetGlobal("gCurrentBuildingBlockString"))} {Canon.Value(_vm.GetGlobal("gCurrentBuildingBlockNumber"))}";
        return new OracleOutcome(_core.Log, Canon.Value(scope), globals, errors, texts);
    }
}

internal static class OracleBb
{
    /// <summary>Runs the same plan through the production interpreter.</summary>
    public static OracleOutcome Run(IReadOnlyList<(string Hook, IReadOnlyList<BbBlock> Blocks)> plan, double level, out long blocksExecuted,
        IReadOnlyList<(string? Table, string Name, double Value)>? seeds = null)
    {
        var core = new OracleCore();
        var interpreter = new BbInterpreter(new OracleHost(core));
        var scope = BbScope.ForHook(OracleCore.Owner, OracleCore.Attacker, OracleCore.Target, OracleCore.Caster, level);
        scope.Unit = OracleCore.Unit;
        scope.Nothing = OracleCore.Nothing;
        scope.Other1 = OracleCore.Other1;
        scope.Other2 = OracleCore.Other2;
        scope.Other3 = OracleCore.Other3;
        foreach (var (table, name, value) in seeds ?? [])
        {
            var target = table is not null && scope.Vars[table] is BbTable named ? named : scope.Vars;
            target[name] = value;
        }
        var errors = 0;
        var texts = new List<string>();
        foreach (var (hook, blocks) in plan)
        {
            core.Mark("== " + hook);
            try { interpreter.Execute(blocks, scope); }
            catch (BbRuntimeException ex)
            {
                errors++;
                texts.Add(ex.Message + " @ " + interpreter.CurrentBlock?.Function);
                core.Mark("!! error");
            }
        }
        blocksExecuted = interpreter.BlocksExecuted;
        var globals = $"{Canon.Value(interpreter.CurrentBlockString)} {Canon.Value((double)interpreter.CurrentBlockNumber)}";
        return new OracleOutcome(core.Log, Canon.Value(scope.Vars), globals, errors, texts);
    }
}
