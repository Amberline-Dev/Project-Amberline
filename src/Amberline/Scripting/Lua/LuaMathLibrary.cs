namespace Amberline.Scripting.Lua;

/// <summary>
/// The <c>math</c> library. <c>math.random</c> and <c>math.randomseed</c> are Lua 5.1's <c>lmathlib.c</c> over the C
/// <c>rand()</c> and <c>srand()</c> of the client's MSVCR80.dll, which the EXE imports. Read from msvcr80 8.0.50727.9672:
/// <c>rand</c> is <c>h = h * 214013 + 2531011; return (h &gt;&gt; 16) &amp; 0x7fff</c> on <c>_getptd()-&gt;_holdrand</c>,
/// <c>srand</c> stores its seed there, and <c>_initptd</c> starts it at 1. Retail runs one game per process, so that state
/// belongs to one match here: a <see cref="CrtState"/> owned by the match's <c>WorldState</c>, shared by every Lua state of the
/// match. A standalone state gets a fresh one at 1. <c>Bot</c> and <c>LevelScript</c> seed it with <c>math.randomseed(os.time())</c>.
/// Reconstruction: the shipped files do not show whether retail's own C code called <c>rand()</c> before the scripts seeded the
/// stream. If it did, retail's stream was advanced by those calls too, and this one is not.
/// </summary>
public static class LuaMathLibrary
{
    /// <summary>MSVCR80's <c>RAND_MAX</c>.</summary>
    private const int RandMax = 0x7fff;

    public static void Open(LuaState state)
    {
        var math = new LuaTable();
        math.Set(LuaValue.Of("abs"), Function("abs", x => Math.Abs(x[0])));
        math.Set(LuaValue.Of("ceil"), Function("ceil", x => Math.Ceiling(x[0])));
        math.Set(LuaValue.Of("floor"), Function("floor", x => Math.Floor(x[0])));
        math.Set(LuaValue.Of("sqrt"), Function("sqrt", x => Math.Sqrt(x[0])));
        math.Set(LuaValue.Of("exp"), Function("exp", x => Math.Exp(x[0])));
        math.Set(LuaValue.Of("log"), Function("log", x => x.Length > 1 ? Math.Log(x[0], x[1]) : Math.Log(x[0])));
        math.Set(LuaValue.Of("log10"), Function("log10", x => Math.Log10(x[0])));
        math.Set(LuaValue.Of("sin"), Function("sin", x => Math.Sin(x[0])));
        math.Set(LuaValue.Of("cos"), Function("cos", x => Math.Cos(x[0])));
        math.Set(LuaValue.Of("tan"), Function("tan", x => Math.Tan(x[0])));
        math.Set(LuaValue.Of("asin"), Function("asin", x => Math.Asin(x[0])));
        math.Set(LuaValue.Of("acos"), Function("acos", x => Math.Acos(x[0])));
        math.Set(LuaValue.Of("atan"), Function("atan", x => Math.Atan(x[0])));
        math.Set(LuaValue.Of("atan2"), Function("atan2", x => Math.Atan2(x[0], x[1])));
        math.Set(LuaValue.Of("pow"), Function("pow", x => Math.Pow(x[0], x[1])));
        math.Set(LuaValue.Of("fmod"), Function("fmod", x => x[0] % x[1]));
        math.Set(LuaValue.Of("deg"), Function("deg", x => x[0] * (180 / Math.PI)));
        math.Set(LuaValue.Of("rad"), Function("rad", x => x[0] * (Math.PI / 180)));
        math.Set(LuaValue.Of("max"), new LuaNative("max", Max));
        math.Set(LuaValue.Of("min"), new LuaNative("min", Min));
        math.Set(LuaValue.Of("frexp"), new LuaNative("frexp", Frexp));
        math.Set(LuaValue.Of("ldexp"), new LuaNative("ldexp", Ldexp));
        math.Set(LuaValue.Of("modf"), new LuaNative("modf", Modf));
        math.Set(LuaValue.Of("random"), new LuaNative("random", Random));
        math.Set(LuaValue.Of("randomseed"), new LuaNative("randomseed", RandomSeed));
        math.Set(LuaValue.Of("huge"), LuaValue.Of(double.PositiveInfinity));
        math.Set(LuaValue.Of("pi"), LuaValue.Of(Math.PI));
        state.SetGlobal("math", LuaValue.Of(math));
    }

    private static LuaValue Function(string name, Func<double[], double> body) =>
        LuaValue.Of(new LuaNative(name, (state, args) =>
        {
            var numbers = new double[args.Length];
            for (var i = 0; i < args.Length; i++)
                numbers[i] = args[i].ToNumber() ?? throw LuaErrors.BadArgument(i + 1, name, "number", args[i]);
            return new[] { LuaValue.Of(body(numbers)) };
        }));

    private static LuaValue[] Max(LuaState state, LuaValue[] args)
    {
        if (args.Length == 0) throw LuaErrors.BadArgument(1, "max", "number", LuaValue.Nil);
        var best = args[0].ToNumber() ?? throw LuaErrors.BadArgument(1, "max", "number", args[0]);
        for (var i = 1; i < args.Length; i++)
        {
            var value = args[i].ToNumber() ?? throw LuaErrors.BadArgument(i + 1, "max", "number", args[i]);
            if (value > best) best = value;
        }
        return new[] { LuaValue.Of(best) };
    }

    private static LuaValue[] Min(LuaState state, LuaValue[] args)
    {
        if (args.Length == 0) throw LuaErrors.BadArgument(1, "min", "number", LuaValue.Nil);
        var best = args[0].ToNumber() ?? throw LuaErrors.BadArgument(1, "min", "number", args[0]);
        for (var i = 1; i < args.Length; i++)
        {
            var value = args[i].ToNumber() ?? throw LuaErrors.BadArgument(i + 1, "min", "number", args[i]);
            if (value < best) best = value;
        }
        return new[] { LuaValue.Of(best) };
    }

    /// <summary><c>math.frexp(x)</c> returns <c>m, e</c> with <c>x = m * 2^e</c> and <c>|m| &lt; 1</c>.</summary>
    private static LuaValue[] Frexp(LuaState state, LuaValue[] args)
    {
        var x = args.Length > 0 ? args[0].ToNumber() ?? throw LuaErrors.BadArgument(1, "frexp", "number", args[0]) : 0;
        if (x == 0 || double.IsNaN(x) || double.IsInfinity(x))
            return new[] { LuaValue.Of(x), LuaValue.Of(0.0) };

        var exponent = (int)Math.Floor(Math.Log(Math.Abs(x), 2)) + 1;
        var mantissa = x / Math.Pow(2, exponent);
        while (Math.Abs(mantissa) >= 1) { mantissa /= 2; exponent++; }
        while (Math.Abs(mantissa) < 0.5 && mantissa != 0) { mantissa *= 2; exponent--; }
        return new[] { LuaValue.Of(mantissa), LuaValue.Of((double)exponent) };
    }

    private static LuaValue[] Ldexp(LuaState state, LuaValue[] args)
    {
        var x = args.Length > 0 ? args[0].ToNumber() ?? throw LuaErrors.BadArgument(1, "ldexp", "number", args[0]) : 0;
        var e = args.Length > 1 ? args[1].ToNumber() ?? throw LuaErrors.BadArgument(2, "ldexp", "number", args[1]) : 0;
        return new[] { LuaValue.Of(x * Math.Pow(2, e)) };
    }

    /// <summary>
    /// <c>math.modf(x)</c> returns the integral and fractional parts, both signed like <c>x</c>:
    /// C's <c>modf</c> truncates toward zero, so -2.5 splits into -2 and -0.5.
    /// </summary>
    private static LuaValue[] Modf(LuaState state, LuaValue[] args)
    {
        var x = args.Length > 0 ? args[0].ToNumber() ?? throw LuaErrors.BadArgument(1, "modf", "number", args[0]) : 0;
        if (double.IsNaN(x)) return new[] { LuaValue.Of(double.NaN), LuaValue.Of(double.NaN) };
        if (double.IsInfinity(x)) return new[] { LuaValue.Of(x), LuaValue.Of(double.IsNegative(x) ? -0.0 : 0.0) };
        var integral = Math.Truncate(x);
        return new[] { LuaValue.Of(integral), LuaValue.Of(x - integral) };
    }

    /// <summary>MSVCR80 <c>rand()</c> on the state of the match this Lua state belongs to.</summary>
    private static int CrtRand(LuaState state)
    {
        var crt = state.CrtRandom;
        crt.HoldRand = unchecked(crt.HoldRand * 214013u + 2531011u);
        return (int)((crt.HoldRand >> 16) & 0x7fff);
    }

    /// <summary>
    /// <c>lmathlib.c</c>: <c>r = (rand() % RAND_MAX) / RAND_MAX</c>. <c>math.random()</c> is <c>r</c>, <c>math.random(u)</c> is
    /// <c>floor(r*u)+1</c> and <c>math.random(l,u)</c> is <c>floor(r*(u-l+1))+l</c>. The draw happens before the argument checks.
    /// </summary>
    private static LuaValue[] Random(LuaState state, LuaValue[] args)
    {
        var r = (double)(CrtRand(state) % RandMax) / RandMax;
        switch (args.Length)
        {
            case 0:
                return new[] { LuaValue.Of(r) };
            case 1:
            {
                var upper = CheckInt(args, 0, "random");
                if (upper < 1) throw new LuaError("bad argument #1 to 'random' (interval is empty)");
                return new[] { LuaValue.Of(Math.Floor(r * upper) + 1) };
            }
            case 2:
            {
                var lower = CheckInt(args, 0, "random");
                var upper = CheckInt(args, 1, "random");
                if (lower > upper) throw new LuaError("bad argument #2 to 'random' (interval is empty)");
                return new[] { LuaValue.Of(Math.Floor(r * unchecked(upper - lower + 1)) + lower) };
            }
            default:
                throw new LuaError("wrong number of arguments");
        }
    }

    /// <summary><c>srand((unsigned int)luaL_checkint(L, 1))</c>: the seed becomes the match's <c>_holdrand</c>.</summary>
    private static LuaValue[] RandomSeed(LuaState state, LuaValue[] args)
    {
        var seed = CheckInt(args, 0, "randomseed");
        state.CrtRandom.HoldRand = unchecked((uint)seed);
        return Array.Empty<LuaValue>();
    }

    /// <summary><c>luaL_checkint</c>: the argument as a number, truncated to a C int.</summary>
    private static int CheckInt(LuaValue[] args, int index, string function)
    {
        var number = index < args.Length ? args[index].ToNumber() : null;
        if (number is null)
            throw LuaErrors.BadArgument(index + 1, function, "number", index < args.Length ? args[index] : LuaValue.Nil);
        return unchecked((int)Math.Truncate(number.Value));
    }
}
