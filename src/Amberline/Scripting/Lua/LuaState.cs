using Amberline.Data;

namespace Amberline.Scripting.Lua;

/// <summary>
/// One Lua 5.1 state: a global table, a call stack and the host boundary. A host creates a state
/// per AI owner (one per unit's script chain), exactly as the BuildingBlocks engine does.
/// Deterministic and package-free: there is no <c>os</c>, <c>io</c>, <c>package</c>,
/// <c>require</c>, <c>loadstring</c> or coroutine library, and no console or file I/O.
/// </summary>
public sealed class LuaState
{
    private readonly LuaInterpreter _interpreter;
    private readonly LuaTable _globals = new();
    private int _nextIdentity = 1;
    private readonly Dictionary<object, string> _identities = new();

    /// <summary>
    /// A Lua state. <paramref name="random"/> is the match's C runtime random state (<c>WorldState.LuaRandom</c>), so every state
    /// of a match draws from one stream; a standalone state gets a fresh one at 1.
    /// </summary>
    public LuaState(CrtState? random = null)
    {
        CrtRandom = random ?? new CrtState();
        _interpreter = new LuaInterpreter(this);
        _globals.Set(LuaValue.Of("_G"), LuaValue.Of(_globals));
        _globals.Set(LuaValue.Of("_VERSION"), LuaValue.Of("Lua 5.1"));
    }

    /// <summary>The random state <c>math.random</c> draws from: the match's, or this state's own when standalone.</summary>
    public CrtState CrtRandom { get; }

    /// <summary>The global table; every <c>GETGLOBAL</c>/<c>SETGLOBAL</c> goes through it.</summary>
    public LuaTable Globals => _globals;

    /// <summary>The live call stack; only the base library reads it (for <c>error</c>'s level and the environments).</summary>
    internal IReadOnlyList<LuaFrame> Frames => _interpreter.Frames;

    /// <summary>
    /// Where <c>print</c> sends its lines. The VM itself performs no I/O; a host that wants
    /// <c>print</c> output installs a sink and otherwise the lines are discarded.
    /// </summary>
    public Action<string>? Output { get; set; }

    internal LuaValue[] HostResults { get; set; } = Array.Empty<LuaValue>();

    /// <summary>Installs the base, <c>math</c>, <c>string</c> and <c>table</c> libraries.</summary>
    public void OpenLibs()
    {
        LuaBaseLibrary.Open(this);
        LuaMathLibrary.Open(this);
        LuaStringLibrary.Open(this);
        LuaTableLibrary.Open(this);
    }

    public void Register(string name, LuaNativeFunc function) =>
        _globals.Set(LuaValue.Of(name), LuaValue.Of(new LuaNative(name, function)));

    public void SetGlobal(string name, LuaValue value) => _globals.Set(LuaValue.Of(name), value);

    public LuaValue GetGlobal(string name) => _globals.Get(LuaValue.Of(name));

    public bool TryGetGlobal(string name, out LuaValue value) => _globals.TryGet(LuaValue.Of(name), out value);

    /// <summary>Wraps a loaded prototype as a closure over the global environment.</summary>
    public LuaClosure Load(LuaChunk chunk) =>
        new(chunk.TopLevel, Array.Empty<LuaUpvalue>(), _globals);

    public LuaClosure Load(string path) => Load(LuaObjectReader.ParseFile(path));

    /// <summary>Runs a chunk's main function and returns its results.</summary>
    public LuaValue[] DoChunk(LuaChunk chunk) => Call(LuaValue.Of(Load(chunk)));

    public LuaValue[] DoChunk(string path) => Call(LuaValue.Of(Load(path)));

    /// <summary>Calls a function with the given arguments and returns every result.</summary>
    public LuaValue[] Call(LuaValue function, params LuaValue[] arguments)
    {
        var saved = HostResults;
        HostResults = Array.Empty<LuaValue>();
        var depth = _interpreter.Frames.Count;
        try
        {
            if (StartCall(function, arguments, null, 0, Multret))
                _interpreter.Execute(depth);
            return HostResults;
        }
        finally
        {
            // An error unwinds through here; the frames it left behind are not the caller's.
            _interpreter.Unwind(depth);
            HostResults = saved;
        }
    }

    public LuaValue[] CallGlobal(string name, params LuaValue[] arguments) =>
        Call(GetGlobal(name), arguments);

    /// <summary>Returns the first result of a call, or nil when there is none.</summary>
    public LuaValue CallOne(LuaValue function, params LuaValue[] arguments)
    {
        var results = Call(function, arguments);
        return results.Length > 0 ? results[0] : LuaValue.Nil;
    }

    /// <summary>The sentinel meaning "as many results as there are" in a CALL/RETURN B or C operand.</summary>
    public const int Multret = -1;

    /// <summary>
    /// Starts a call. Returns <see langword="true"/> when a Lua frame was pushed (or a tail frame
    /// reused) and the caller must yield to the interpreter loop; <see langword="false"/> when the
    /// callee was a native and its results are already placed.
    /// </summary>
    internal bool StartCall(LuaValue function, LuaValue[] arguments, LuaFrame? caller, int resultBase, int wantResults, LuaFrame? reuseFrame = null)
    {
        var target = function;
        var args = arguments;
        for (var depth = 0; target.Type != LuaType.Function; depth++)
        {
            var metamethod = MetamethodOf(target, "__call");
            if (metamethod.IsNil) throw LuaErrors.TypeError(target, "call");
            if (depth > 100) throw new LuaError("loop in call");
            var withSelf = new LuaValue[args.Length + 1];
            withSelf[0] = target;
            Array.Copy(args, 0, withSelf, 1, args.Length);
            args = withSelf;
            target = metamethod;
        }

        switch (target.AsFunction)
        {
            case LuaNative native:
                var results = native.Invoke(this, args);
                if (results is null) results = Array.Empty<LuaValue>();
                PlaceResults(caller, resultBase, wantResults, results);
                return false;

            case LuaClosure closure:
                if (reuseFrame is not null) reuseFrame.Reuse(closure, args);
                else
                {
                    var frame = new LuaFrame(closure, caller, resultBase, wantResults);
                    frame.Enter(args);
                    _interpreter.Push(frame);
                }
                return true;

            default:
                throw new LuaError("unknown function kind");
        }
    }

    /// <summary>Runs a function to one result; used for function-valued metamethods.</summary>
    internal LuaValue CallMetamethod(LuaFunction function, params LuaValue[] arguments) =>
        CallOne(LuaValue.Of(function), arguments);

    /// <summary>Places a call's results in the caller's registers, or in <see cref="HostResults"/>.</summary>
    internal void PlaceResults(LuaFrame? caller, int resultBase, int wantResults, LuaValue[] results)
    {
        if (caller is null)
        {
            HostResults = wantResults == Multret ? results : Trim(results, wantResults);
            return;
        }

        var registers = caller.Registers;
        if (wantResults == Multret)
        {
            for (var i = 0; i < results.Length; i++) registers[resultBase + i] = results[i];
            caller.Top = resultBase + results.Length;
        }
        else
        {
            for (var i = 0; i < wantResults; i++)
                registers[resultBase + i] = i < results.Length ? results[i] : LuaValue.Nil;
            caller.Top = resultBase + wantResults;
        }
    }

    private static LuaValue[] Trim(LuaValue[] results, int count)
    {
        if (results.Length == count) return results;
        var trimmed = new LuaValue[count];
        Array.Copy(results, trimmed, Math.Min(count, results.Length));
        return trimmed;
    }

    // ---- value operations with metamethods ----

    /// <summary><c>GETTABLE</c>: raw lookup first, then <c>__index</c> (a function, or another table to repeat).</summary>
    internal LuaValue Index(LuaValue target, LuaValue key)
    {
        for (var loop = 0; loop < 100; loop++)
        {
            if (target.Type == LuaType.Table)
            {
                var table = target.AsTable;
                if (table.TryGet(key, out var value)) return value;
                var metamethod = Metamethod(table.Metatable, "__index");
                if (metamethod.IsNil) return LuaValue.Nil;
                if (metamethod.Type == LuaType.Function) return CallMetamethod(metamethod.AsFunction, target, key);
                target = metamethod;
                continue;
            }

            var fallback = MetamethodOf(target, "__index");
            if (fallback.IsNil) throw LuaErrors.TypeError(target, "index");
            if (fallback.Type == LuaType.Function) return CallMetamethod(fallback.AsFunction, target, key);
            target = fallback;
        }
        throw new LuaError("loop in gettable");
    }

    /// <summary><c>SETTABLE</c>: raw store first, then <c>__newindex</c>.</summary>
    internal void NewIndex(LuaValue target, LuaValue key, LuaValue value)
    {
        for (var loop = 0; loop < 100; loop++)
        {
            if (target.Type == LuaType.Table)
            {
                var table = target.AsTable;
                if (table.ContainsKey(key))
                {
                    table.Set(key, value);
                    return;
                }
                var metamethod = Metamethod(table.Metatable, "__newindex");
                if (metamethod.IsNil)
                {
                    table.Set(key, value);
                    return;
                }
                if (metamethod.Type == LuaType.Function)
                {
                    CallMetamethod(metamethod.AsFunction, target, key, value);
                    return;
                }
                target = metamethod;
                continue;
            }

            var fallback = MetamethodOf(target, "__newindex");
            if (fallback.IsNil) throw LuaErrors.TypeError(target, "index");
            if (fallback.Type == LuaType.Function)
            {
                CallMetamethod(fallback.AsFunction, target, key, value);
                return;
            }
            target = fallback;
        }
        throw new LuaError("loop in settable");
    }

    internal LuaValue LengthOf(LuaValue value)
    {
        if (value.Type == LuaType.String) return LuaValue.Of((double)value.AsString.Length);
        if (value.Type == LuaType.Table)
        {
            // luaV_objlen consults TM_LEN first and only then falls back to luaH_getn's border.
            var metamethod = Metamethod(value.AsTable.Metatable, "__len");
            if (!metamethod.IsNil) return CallMetamethod(metamethod.AsFunction, value);
            return LuaValue.Of((double)value.AsTable.Length);
        }
        var fallback = MetamethodOf(value, "__len");
        if (!fallback.IsNil) return CallMetamethod(fallback.AsFunction, value);
        throw LuaErrors.TypeError(value, "get length of");
    }

    internal LuaValue Concat(LuaValue left, LuaValue right)
    {
        if (IsConcatenable(left) && IsConcatenable(right))
            return LuaValue.Of(left.ToPrintableString() + right.ToPrintableString());

        var metamethod = BinaryMetamethod("__concat", left, right);
        if (metamethod is not null) return CallMetamethod(metamethod, left, right);
        throw LuaErrors.TypeError(IsConcatenable(left) ? right : left, "concatenate");
    }

    private static bool IsConcatenable(LuaValue value) =>
        value.Type is LuaType.String or LuaType.Number;

    /// <summary>Plain <c>==</c>: types must match, tables and functions compare by reference.</summary>
    internal bool Equal(LuaValue left, LuaValue right)
    {
        if (left.Type != right.Type) return false;
        if (left.Type is LuaType.Table or LuaType.Function)
        {
            if (ReferenceEquals(left.RawReference, right.RawReference)) return true;
            var metamethod = BinaryMetamethod("__eq", left, right);
            if (metamethod is not null) return CallMetamethod(metamethod, left, right).Truthy;
            return false;
        }
        return left.Equals(right);
    }

    internal bool OrderLess(LuaValue left, LuaValue right) => Order(left, right, "__lt", (x, y) => x < y);

    internal bool OrderLessEqual(LuaValue left, LuaValue right) => Order(left, right, "__le", (x, y) => x <= y);

    private bool Order(LuaValue left, LuaValue right, string name, Func<double, double, bool> onNumbers)
    {
        if (left.Type != right.Type) throw LuaErrors.Order(left, right);
        switch (left.Type)
        {
            case LuaType.Number:
                return onNumbers(left.AsNumber, right.AsNumber);
            case LuaType.String:
                var order = LuaStrings.CompareOrdinalBytes(left.AsString, right.AsString);
                return name == "__lt" ? order < 0 : order <= 0;
            default:
                var metamethod = BinaryMetamethod(name, left, right);
                if (metamethod is not null) return CallMetamethod(metamethod, left, right).Truthy;
                throw LuaErrors.Order(left, right);
        }
    }

    /// <summary>The first of <paramref name="name"/> found on either operand's metatable.</summary>
    internal LuaFunction? BinaryMetamethod(string name, LuaValue left, LuaValue right)
    {
        var onLeft = MetamethodOf(left, name);
        if (!onLeft.IsNil) return onLeft.AsFunction;
        var onRight = MetamethodOf(right, name);
        return onRight.IsNil ? null : onRight.AsFunction;
    }

    internal static LuaValue MetamethodOf(LuaValue value, string name) => value.Type switch
    {
        LuaType.Table => Metamethod(value.AsTable.Metatable, name),
        _ => LuaValue.Nil,
    };

    internal static LuaValue Metamethod(LuaTable? metatable, string name) =>
        metatable is null ? LuaValue.Nil : metatable.Get(LuaValue.Of(name));

    /// <summary>The metatable object itself, or nil; <c>getmetatable</c> layers <c>__metatable</c> on top.</summary>
    internal static LuaValue MetaTableOf(LuaValue value) =>
        value.Type == LuaType.Table && value.AsTable.Metatable is LuaTable metatable
            ? LuaValue.Of(metatable)
            : LuaValue.Nil;

    /// <summary>
    /// <c>tostring</c>: <c>__tostring</c> when the value carries one (<c>luaB_tostring</c> calls it
    /// first), otherwise the type's own text or a <c>table: 0x1</c>-style address. That address is
    /// a per-state sequential id, not a real pointer — <c>DIAGNOSTICS:</c> retail prints <c>%p</c>,
    /// and a fixed id keeps the interpreter and its checks deterministic. No shipped script reads the text: the
    /// only shipped <c>tostring</c> is BuildingBlocksBase's debug <c>ToSay</c> (protos 0.74 and 0.75), which concatenates it into a Say line.
    /// </summary>
    internal static string ToDisplayString(LuaState state, LuaValue value)
    {
        var metamethod = MetamethodOf(value, "__tostring");
        if (!metamethod.IsNil && metamethod.Type == LuaType.Function)
            return state.CallMetamethod(metamethod.AsFunction, value).ToPrintableString();
        return value.Type switch
        {
            LuaType.String => value.AsString,
            LuaType.Nil => "nil",
            LuaType.Boolean => value.AsBoolean ? "true" : "false",
            LuaType.Number => LuaStrings.NumberToString(value.AsNumber),
            LuaType.Table => $"table: 0x{state.Identity(value.AsTable)}",
            _ => $"function: 0x{state.Identity(value.AsFunction)}",
        };
    }

    /// <summary>A stable per-state address-like label for <c>tostring</c>.</summary>
    internal string Identity(object reference)
    {
        if (_identities.TryGetValue(reference, out var existing)) return existing;
        var created = _nextIdentity++.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _identities[reference] = created;
        return created;
    }
}
