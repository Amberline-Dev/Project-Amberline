using System.Reflection;
using Amberline.Core;
using Amberline.Scripting.Lua;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests.Scripting;

/// <summary>
/// Lua 5.1 <c>math.random</c> over MSVCR80's <c>rand()</c>. Retail runs each game in its own process, and the CRT state (<c>_holdrand</c>,
/// set to 1 by <c>_initptd</c>) belongs to that process. Here it belongs to the match (<c>WorldState.LuaRandom</c>, a
/// <see cref="CrtState"/>), shared by all of the match's Lua states; a standalone state starts at 1. The expected numbers are the classic
/// MSVC sequence (seed 1: 41, 18467, 6334, 26500, 19169), not values from our own generator: <c>draw = rand() % 32767 / 32767</c>.
/// </summary>
public static class LuaMathStreamChecks
{
    private const double RandMax = 32767.0;

    [Check("scripting.lua-math-random-is-crt-rand")]
    public static void StandaloneStateDrawsTheCrtSequence()
    {
        var state = NewState();
        var (_, random) = MathFunctions(state);
        var seedOne = new[] { 41, 18467, 6334, 26500, 19169 };
        for (var i = 0; i < seedOne.Length; i++)
            TestAssert.Near(seedOne[i] / RandMax, Draw(state, random), 1e-12, $"draw {i + 1} of a standalone state (seed 1)");
    }

    /// <summary>
    /// The BuildingBlocks host's <c>Random()</c> (<c>IBbGameHost.Random</c>, batch 222) is Lua's <c>math.random()</c> on the match's CRT state, not
    /// the engine's match RNG: from a reset state it draws the seed-1 sequence above and advances <c>WorldState.LuaRandom</c> by one step.
    /// </summary>
    [Check("scripting.bb-host-random-is-the-match-lua-stream")]
    public static void BuildingBlocksRandomDrawsTheMatchLuaStream()
    {
        using var probe = new BbSlotChecks.Probe("Soraka");
        var host = (BbSpellHost)typeof(BbChampionModule).GetProperty("Host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(probe.Match.Module)!;
        var crt = probe.Match.Runtime.World.LuaRandom;
        crt.HoldRand = 1;
        var seedOne = new[] { 41, 18467, 6334 };
        for (var i = 0; i < seedOne.Length; i++)
            TestAssert.Near(seedOne[i] / RandMax, host.Random(), 1e-12, $"BuildingBlocks draw {i + 1} of the match stream (seed 1)");
        crt.HoldRand = 1;
        host.Random();
        TestAssert.Equal(2745024u, crt.HoldRand);
    }

    [Check("scripting.lua-math-stream-is-per-match")]
    public static void StatesOfOneMatchShareOneStream()
    {
        var match = new CrtState();
        var first = NewState(match);
        var second = NewState(match);
        var (seedFirst, randomFirst) = MathFunctions(first);
        var (_, randomSecond) = MathFunctions(second);
        first.Call(seedFirst, LuaValue.Of(42.0));
        TestAssert.Near(175 / RandMax, Draw(first, randomFirst), 1e-12, "the first draw of seed 42");
        TestAssert.Near(400 / RandMax, Draw(second, randomSecond), 1e-12, "the second state of the match continues the stream");
        TestAssert.Near(17869 / RandMax, Draw(first, randomFirst), 1e-12, "and the first state continues it too");

        // A standalone state is not part of the match: it starts at seed 1 whatever the match has drawn.
        var standalone = NewState();
        var (_, randomStandalone) = MathFunctions(standalone);
        TestAssert.Near(41 / RandMax, Draw(standalone, randomStandalone), 1e-12, "a standalone state starts at seed 1");
    }

    [Check("scripting.lua-math-two-matches-draw-alike")]
    public static void MatchesBuiltOneAfterAnotherDrawAlike()
    {
        // Both matches are built on this thread, one after the other. The first draws five values; none of them reaches the second.
        var firstMatch = MatchDraws(new WorldState(), 5);
        var secondMatch = MatchDraws(new WorldState(), 3);
        var seedOne = new[] { 41, 18467, 6334 };
        for (var i = 0; i < seedOne.Length; i++)
        {
            TestAssert.Near(seedOne[i] / RandMax, secondMatch[i], 1e-12, $"the second match's draw {i + 1} is seed 1's");
            TestAssert.Near(firstMatch[i], secondMatch[i], 1e-12, $"and equals the first match's draw {i + 1}");
        }
    }

    [Check("scripting.lua-math-random-integer-forms")]
    public static void IntegerFormsAndArgumentChecks()
    {
        var state = NewState();
        var (seed, random) = MathFunctions(state);

        // math.random(u) = floor(r*u)+1 and math.random(l,u) = floor(r*(u-l+1))+l with r = 41/32767 (the first draw of seed 1).
        state.Call(seed, LuaValue.Of(1.0));
        TestAssert.Equal(1.0, Call(state, random, 10.0), "math.random(10) of the first draw");
        state.Call(seed, LuaValue.Of(1.0));
        TestAssert.Equal(3.0, Call(state, random, 3.0, 9.0), "math.random(3, 9) of the first draw");

        // luaL_checkint truncates: math.random(3.7) is math.random(3). The second draw (18467/32767) gives 2 for u = 3.
        state.Call(seed, LuaValue.Of(1.0));
        Draw(state, random);
        TestAssert.Equal(2.0, Call(state, random, 3.7), "math.random(3.7) truncates to math.random(3)");

        // The draw happens before the argument checks: a rejected call still advances the stream.
        state.Call(seed, LuaValue.Of(1.0));
        TestAssert.Equal("bad argument #1 to 'random' (interval is empty)",
            ErrorOf(() => Call(state, random, 0.0)), "math.random(0) is rejected");
        TestAssert.Near(18467 / RandMax, Draw(state, random), 1e-12, "the rejected call consumed the first draw");
        TestAssert.Equal("bad argument #2 to 'random' (interval is empty)",
            ErrorOf(() => Call(state, random, 5.0, 4.0)), "math.random(5, 4) is rejected at argument 2");
        TestAssert.True(ErrorOf(() => state.Call(seed)).StartsWith("bad argument #1 to 'randomseed'", StringComparison.Ordinal),
            "math.randomseed() needs its argument (luaL_checkint)");
    }

    private static LuaState NewState(CrtState? random = null)
    {
        var state = new LuaState(random);
        state.OpenLibs();
        return state;
    }

    private static (LuaValue Seed, LuaValue Random) MathFunctions(LuaState state)
    {
        var math = state.GetGlobal("math").AsTable;
        return (math.Get(LuaValue.Of("randomseed")), math.Get(LuaValue.Of("random")));
    }

    /// <summary>The first <paramref name="count"/> zero-argument draws of a new Lua state of <paramref name="match"/>.</summary>
    private static double[] MatchDraws(WorldState match, int count)
    {
        var state = NewState(match.LuaRandom);
        var (_, random) = MathFunctions(state);
        return Enumerable.Range(0, count).Select(_ => Draw(state, random)).ToArray();
    }

    /// <summary>The zero-argument form: <c>(rand() % RAND_MAX) / RAND_MAX</c>.</summary>
    private static double Draw(LuaState state, LuaValue random) => Call(state, random);

    private static double Call(LuaState state, LuaValue function, params double[] arguments) =>
        state.Call(function, arguments.Select(argument => LuaValue.Of(argument)).ToArray())[0].ToNumber()
            ?? throw new InvalidOperationException("math.random returned no number");

    private static string ErrorOf(Action action)
    {
        try
        {
            action();
        }
        catch (LuaError error)
        {
            return error.Message;
        }
        return "no error";
    }
}
