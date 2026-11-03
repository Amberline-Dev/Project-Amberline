namespace Amberline.Scripting.Lua;

/// <summary>
/// The part of the Lua 5.1 base library a script engine has no business replacing. Absent by
/// design: <c>os</c>, <c>io</c>, <c>package</c>, <c>require</c>, <c>loadstring</c>, <c>loadfile</c>,
/// <c>dofile</c>, <c>collectgarbage</c> and the coroutines — the game registers its own
/// <c>os.time</c>/<c>dofile</c> as host natives.
/// </summary>
public static class LuaBaseLibrary
{
    public static void Open(LuaState state)
    {
        state.Register("assert", Assert);
        state.Register("error", Error);
        state.Register("getfenv", GetFenv);
        state.Register("setfenv", SetFenv);
        state.Register("getmetatable", GetMetatable);
        state.Register("setmetatable", SetMetatable);
        state.Register("ipairs", IPairs);
        state.Register("pairs", Pairs);
        state.Register("next", Next);
        state.Register("pcall", PCall);
        state.Register("print", Print);
        state.Register("select", Select);
        state.Register("tonumber", ToNumber);
        state.Register("tostring", ToString);
        state.Register("type", Type);
        state.Register("unpack", Unpack);
        state.Register("rawget", RawGet);
        state.Register("rawset", RawSet);
        state.Register("rawequal", RawEqual);
    }

    private static LuaValue[] Assert(LuaState state, LuaValue[] args)
    {
        if (args.Length == 0) throw LuaErrors.BadArgument(1, "assert", "value", LuaValue.Nil);
        if (args[0].Truthy) return args;
        throw new LuaError(args.Length > 1 ? args[1] : LuaValue.Of("assertion failed!"));
    }

    /// <summary><c>error(message, level)</c>: <c>level</c> selects which frame's position is prefixed.</summary>
    private static LuaValue[] Error(LuaState state, LuaValue[] args)
    {
        var message = args.Length > 0 ? args[0] : LuaValue.Nil;
        var level = args.Length > 1 ? (int)(args[1].ToNumber() ?? 1) : 1;
        if (message.Type == LuaType.String && level > 0)
        {
            // error() already carries its chunk:line: prefix, so the error is marked located and
            // the interpreter does not attach another one.
            message = LuaValue.Of(Where(state, level) + message.AsString);
        }
        throw LuaError.Raised(message);
    }

    private static string Where(LuaState state, int level)
    {
        var frames = state.Frames;
        if (level < 1 || level > frames.Count) return string.Empty;
        var frame = frames[frames.Count - level];
        var line = frame.LineAt(frame.Pc > 0 ? frame.Pc - 1 : 0);
        return line > 0 ? $"{LuaErrors.ChunkLabel(frame.Prototype.Source ?? "?")}:{line}: " : string.Empty;
    }

    private static LuaValue[] PCall(LuaState state, LuaValue[] args)
    {
        if (args.Length == 0) throw LuaErrors.BadArgument(1, "pcall", "value", LuaValue.Nil);
        try
        {
            var results = state.Call(args[0], args[1..]);
            var combined = new LuaValue[results.Length + 1];
            combined[0] = LuaValue.Of(true);
            Array.Copy(results, 0, combined, 1, results.Length);
            return combined;
        }
        catch (LuaError error)
        {
            return new[] { LuaValue.Of(false), error.Value };
        }
    }

    private static LuaValue[] Print(LuaState state, LuaValue[] args)
    {
        var parts = new string[args.Length];
        for (var i = 0; i < args.Length; i++) parts[i] = LuaState.ToDisplayString(state, args[i]);
        state.Output?.Invoke(string.Join("\t", parts));
        return Array.Empty<LuaValue>();
    }

    private static LuaValue[] ToString(LuaState state, LuaValue[] args)
    {
        if (args.Length == 0) throw LuaErrors.BadArgument(1, "tostring", "value", LuaValue.Nil);
        return new[] { LuaValue.Of(LuaState.ToDisplayString(state, args[0])) };
    }

    private static LuaValue[] Type(LuaState state, LuaValue[] args)
    {
        if (args.Length == 0) throw LuaErrors.BadArgument(1, "type", "value", LuaValue.Nil);
        return new[] { LuaValue.Of(args[0].TypeName()) };
    }

    private static LuaValue[] ToNumber(LuaState state, LuaValue[] args)
    {
        if (args.Length == 0) throw LuaErrors.BadArgument(1, "tonumber", "value", LuaValue.Nil);
        if (args.Length < 2)
            return new[] { args[0].ToNumber() is double value ? LuaValue.Of(value) : LuaValue.Nil };

        var number = args[1].ToNumber() ?? throw LuaErrors.BadArgument(2, "tonumber", "number", args[1]);
        var text = args[0].Type switch
        {
            LuaType.String => args[0].AsString,
            LuaType.Number => LuaStrings.NumberToString(args[0].AsNumber),
            _ => null,
        };
        if (text is null) return new[] { LuaValue.Nil };
        var trimmed = text.Trim(' ', '\t', '\n', '\v', '\f', '\r');
        return new[] { ParseInBase(trimmed, (int)number) is double parsed ? LuaValue.Of(parsed) : LuaValue.Nil };
    }

    private static double? ParseInBase(string text, int baseValue)
    {
        if (baseValue is < 2 or > 36) return null;
        if (text.Length == 0) return null;
        var start = 0;
        var negative = text[0] == '-';
        if (text[0] is '+' or '-') start = 1;
        if (start >= text.Length) return null;

        double value = 0;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            var digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'z' => c - 'a' + 10,
                >= 'A' and <= 'Z' => c - 'A' + 10,
                _ => -1,
            };
            if (digit < 0 || digit >= baseValue) return null;
            value = value * baseValue + digit;
        }
        return negative ? -value : value;
    }

    private static LuaValue[] Select(LuaState state, LuaValue[] args)
    {
        if (args.Length == 0) throw LuaErrors.BadArgument(1, "select", "value", LuaValue.Nil);
        if (args[0].Type == LuaType.String && args[0].AsString == "#")
            return new[] { LuaValue.Of((double)(args.Length - 1)) };

        var index = args[0].ToNumber() ?? throw LuaErrors.BadArgument(1, "select", "number", args[0]);
        var start = index < 0 ? args.Length + (int)index : (int)index;
        if (start < 1) throw new LuaError("bad argument #1 to 'select' (index out of range)");
        if (start > args.Length) return Array.Empty<LuaValue>();
        return args[start..];
    }

    private static LuaValue[] Unpack(LuaState state, LuaValue[] args)
    {
        if (args.Length == 0 || args[0].Type != LuaType.Table)
            throw LuaErrors.BadArgument(1, "unpack", "table", args.Length == 0 ? LuaValue.Nil : args[0]);
        var table = args[0].AsTable;
        var first = args.Length > 1 ? args[1].ToNumber() ?? 1 : 1;
        var last = args.Length > 2 ? args[2].ToNumber() ?? table.Length : table.Length;
        var count = (int)(last - first + 1);
        if (count <= 0) return Array.Empty<LuaValue>();
        var results = new LuaValue[count];
        for (var i = 0; i < count; i++) results[i] = table.Get(LuaValue.Of(first + i));
        return results;
    }

    private static LuaValue[] RawGet(LuaState state, LuaValue[] args)
    {
        if (args.Length < 2 || args[0].Type != LuaType.Table)
            throw LuaErrors.BadArgument(1, "rawget", "table", args.Length == 0 ? LuaValue.Nil : args[0]);
        return new[] { args[0].AsTable.Get(args[1]) };
    }

    private static LuaValue[] RawSet(LuaState state, LuaValue[] args)
    {
        if (args.Length < 3 || args[0].Type != LuaType.Table)
            throw LuaErrors.BadArgument(1, "rawset", "table", args.Length == 0 ? LuaValue.Nil : args[0]);
        args[0].AsTable.Set(args[1], args[2]);
        return new[] { args[0] };
    }

    private static LuaValue[] RawEqual(LuaState state, LuaValue[] args)
    {
        var left = args.Length > 0 ? args[0] : LuaValue.Nil;
        var right = args.Length > 1 ? args[1] : LuaValue.Nil;
        return new[] { LuaValue.Of(left.Equals(right)) };
    }

    private static LuaValue[] GetMetatable(LuaState state, LuaValue[] args)
    {
        if (args.Length == 0) throw LuaErrors.BadArgument(1, "getmetatable", "value", LuaValue.Nil);
        return new[] { LuaState.MetamethodOf(args[0], "__metatable") is var locked && !locked.IsNil
            ? locked
            : LuaState.MetaTableOf(args[0]) };
    }

    private static LuaValue[] SetMetatable(LuaState state, LuaValue[] args)
    {
        if (args.Length == 0 || args[0].Type != LuaType.Table)
            throw LuaErrors.BadArgument(1, "setmetatable", "table", args.Length == 0 ? LuaValue.Nil : args[0]);
        if (args.Length < 2 || args[1].IsNil)
            args[0].AsTable.Metatable = null;
        else if (args[1].Type == LuaType.Table)
            args[0].AsTable.Metatable = args[1].AsTable;
        else
            throw LuaErrors.BadArgument(2, "setmetatable", "nil or table", args[1]);
        return new[] { args[0] };
    }

    private static LuaValue[] Pairs(LuaState state, LuaValue[] args)
    {
        if (args.Length == 0 || args[0].Type != LuaType.Table)
            throw LuaErrors.BadArgument(1, "pairs", "table", args.Length == 0 ? LuaValue.Nil : args[0]);
        return new[] { state.GetGlobal("next"), args[0], LuaValue.Nil };
    }

    private static LuaValue[] Next(LuaState state, LuaValue[] args)
    {
        if (args.Length == 0 || args[0].Type != LuaType.Table)
            throw LuaErrors.BadArgument(1, "next", "table", args.Length == 0 ? LuaValue.Nil : args[0]);
        var table = args[0].AsTable;
        var key = args.Length > 1 ? args[1] : LuaValue.Nil;
        var successor = table.Next(key);
        if (successor.IsNil) return Array.Empty<LuaValue>();
        return new[] { successor, table.Get(successor) };
    }

    private static LuaValue[] IPairs(LuaState state, LuaValue[] args)
    {
        if (args.Length == 0 || args[0].Type != LuaType.Table)
            throw LuaErrors.BadArgument(1, "ipairs", "table", args.Length == 0 ? LuaValue.Nil : args[0]);
        return new[] { LuaValue.Of(new LuaNative("ipairsaux", IPairsAux)), args[0], LuaValue.Of(0.0) };
    }

    private static LuaValue[] IPairsAux(LuaState state, LuaValue[] args)
    {
        var table = args[0].AsTable;
        var index = (args[1].ToNumber() ?? 0) + 1;
        // ipairs walks the array part raw, ignoring __index, exactly like lua_rawgeti.
        var value = table.Get(LuaValue.Of(index));
        return value.IsNil ? Array.Empty<LuaValue>() : new[] { LuaValue.Of(index), value };
    }

    private static LuaValue[] GetFenv(LuaState state, LuaValue[] args)
    {
        var target = args.Length > 0 ? args[0] : LuaValue.Of(1.0);
        if (target.Type == LuaType.Number)
        {
            var level = (int)target.AsNumber;
            if (level == 0) return new[] { LuaValue.Of(state.Globals) };
            var frames = state.Frames;
            if (level < 1 || level > frames.Count) return new[] { LuaValue.Nil };
            return new[] { LuaValue.Of(frames[frames.Count - level].Closure.Environment) };
        }
        if (target.Type == LuaType.Function)
            return new[] { LuaValue.Of(target.AsFunction is LuaClosure closure ? closure.Environment : state.Globals) };
        throw LuaErrors.BadArgument(1, "getfenv", "function or level", target);
    }

    private static LuaValue[] SetFenv(LuaState state, LuaValue[] args)
    {
        if (args.Length < 2 || args[1].Type != LuaType.Table)
            throw LuaErrors.BadArgument(2, "setfenv", "table", args.Length < 2 ? LuaValue.Nil : args[1]);
        var environment = args[1].AsTable;
        var target = args.Length > 0 ? args[0] : LuaValue.Of(1.0);
        if (target.Type == LuaType.Number)
        {
            var level = (int)target.AsNumber;
            if (level == 0) throw new LuaError("cannot set the environment of the running thread");
            var frames = state.Frames;
            if (level < 1 || level > frames.Count) return new[] { target };
            frames[frames.Count - level].Closure.Environment = environment;
            return new[] { target };
        }
        if (target.Type == LuaType.Function && target.AsFunction is LuaClosure closure)
        {
            closure.Environment = environment;
            return new[] { target };
        }
        throw LuaErrors.BadArgument(1, "setfenv", "function or level", target);
    }
}
