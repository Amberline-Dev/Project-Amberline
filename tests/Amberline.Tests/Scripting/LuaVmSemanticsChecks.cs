using Amberline.Data;
using Amberline.Scripting.Lua;
// Amberline.Data has a LuaValue record for the listing side; the runtime one wins here.
using LuaValue = Amberline.Scripting.Lua.LuaValue;

namespace Amberline.Tests;

/// <summary>
/// Opcode and semantics pins for the Lua 5.1 interpreter. The shipped corpus is bytecode-only and
/// the tree has no compiler, so every prototype here is hand-assembled from the <c>lopcodes.h</c>
/// encodings. The operand shapes used are the ones read off the bytecode listing of NeutralMinionSpawn
/// and Minion: SETLIST's <c>(C-1)*50</c> block index, TFORLOOP followed by exactly one JMP,
/// FORPREP/FORLOOP sBx counted from the instruction after, and EQ/LT/LE's A field.
/// </summary>
public static class LuaVmSemanticsChecks
{
    [Check("scripting.lua-vm-semantics")]
    public static void OpcodesAndSemanticsMatchLua51()
    {
        ArithmeticAndNumericCoercion();
        RemainderTakesTheDivisorSign();
        ConcatPresentsNumbersAsLuaiDoes();
        ComparisonsAndTheJumpsTheySkip();
        NumericForRunsInBothDirections();
        GenericForUsesTheFollowingJump();
        ClosuresShareOpenUpvalues();
        VarargsAndMultipleResults();
        TailCallsReuseTheFrame();
        SetListSpillsAcrossItsBlocks();
        MetatablesSupplyMissingOperations();
        ErrorsCarryTheChunkAndLine();
    }

    // ---- arithmetic ------------------------------------------------------------------------------

    private static void ArithmeticAndNumericCoercion()
    {
        TestAssert.Equal(5.0, Number(Bin(LuaOpcode.Add, N(2), N(3))), "2+3");
        TestAssert.Equal(-1.0, Number(Bin(LuaOpcode.Sub, N(2), N(3))), "2-3");
        TestAssert.Equal(6.0, Number(Bin(LuaOpcode.Mul, N(2), N(3))), "2*3");
        TestAssert.Equal(1.5, Number(Bin(LuaOpcode.Div, N(3), N(2))), "3/2");
        TestAssert.Equal(9.0, Number(Bin(LuaOpcode.Pow, N(3), N(2))), "3^2");

        var unm = new LuaAsm();
        unm.Load(0, 2);
        unm.Unary(LuaOpcode.Unm, 1, 0);
        unm.Return(1, 2);
        TestAssert.Equal(-2.0, Number(unm), "-2");

        // Arithmetic coerces numeric strings before it fails, exactly as luaO_arith does.
        TestAssert.Equal(2.5, Number(Bin(LuaOpcode.Div, S("10"), S("4"))), "\"10\"/\"4\"");
        TestAssert.Equal(40.0, Number(Bin(LuaOpcode.Mul, S("10"), S("4"))), "\"10\"*\"4\"");
        TestAssert.Equal(3.0, Number(Bin(LuaOpcode.Add, S("1"), N(2))), "\"1\"+2");
        TestAssert.Equal(7.0, Number(Bin(LuaOpcode.Add, S(" 7 "), N(0))), "\" 7 \"+0");

        var wrong = ErrorFrom(() => Run(Bin(LuaOpcode.Add, LuaValue.Nil, N(1))));
        TestAssert.Equal("semantics.lua:1: attempt to perform arithmetic on a nil value", wrong.Message, "nil + 1");
    }

    private static void RemainderTakesTheDivisorSign()
    {
        // luaV_mod keeps the sign of the divisor, unlike C# `%` (and unlike math.fmod).
        TestAssert.Equal(1.0, Number(Bin(LuaOpcode.Mod, N(10), N(3))), "10%3");
        TestAssert.Equal(-2.0, Number(Bin(LuaOpcode.Mod, N(10), N(-3))), "10%-3");
        TestAssert.Equal(2.0, Number(Bin(LuaOpcode.Mod, N(-10), N(3))), "-10%3");
        TestAssert.Equal(-1.0, Number(Bin(LuaOpcode.Mod, N(-10), N(-3))), "-10%-3");
        TestAssert.Equal(0.5, Number(Bin(LuaOpcode.Mod, N(2.5), N(2))), "2.5%2");
        TestAssert.Equal(1.0, Number(Bin(LuaOpcode.Mod, S("10"), S("3"))), "\"10\"%\"3\"");
    }

    private static void ConcatPresentsNumbersAsLuaiDoes()
    {
        // LUAI_NUMFFORMAT is %.14g: fourteen significant digits, trailing zeros trimmed.
        TestAssert.Equal("12", Concat(N(1), N(2)), "1 .. 2");
        TestAssert.Equal("1", Concat(N(1), S("")), "1 .. ''");
        TestAssert.Equal("1.5", Concat(N(1.5), S("")), "1.5 .. ''");
        TestAssert.Equal("0.1", Concat(N(0.1), S("")), "0.1 .. ''");
        TestAssert.Equal("100", Concat(N(100), S("")), "100 .. ''");
        TestAssert.Equal("-0.25", Concat(N(-0.25), S("")), "-0.25 .. ''");
        TestAssert.Equal("1e+20", Concat(N(1e20), S("")), "1e20 .. ''");
        TestAssert.Equal("0.33333333333333", Concat(N(1.0 / 3), S("")), "(1/3) .. ''");

        var wrong = ErrorFrom(() => Run(ConcatTable()));
        TestAssert.Equal("semantics.lua:1: attempt to concatenate a table value", wrong.Message);
    }

    /// <summary><c>{} .. ''</c>: a table is not concatenatable.</summary>
    private static LuaAsm ConcatTable()
    {
        var asm = new LuaAsm();
        asm.NewTable(0);
        asm.Load(1, "");
        asm.Concat(2, 0, 1);
        asm.Return(2, 2);
        return asm;
    }

    private static void ComparisonsAndTheJumpsTheySkip()
    {
        // `if a == b then "then" end "else"` is EQ A=0 plus a JMP over the then-arm, the shape of
        // Minion's `if GetState() == AI_HALTED then return end`.
        TestAssert.Equal("then", Branch(N(2), N(2), LuaOpcode.Equal, 0), "EQ A=0, equal");
        TestAssert.Equal("else", Branch(N(2), N(3), LuaOpcode.Equal, 0), "EQ A=0, different");

        // `if a ~= nil then body end` is EQ A=1 plus a JMP: the JMP runs when they are equal.
        TestAssert.Equal("then", Branch(N(2), LuaValue.Nil, LuaOpcode.Equal, 1), "EQ A=1, different");
        TestAssert.Equal("else", Branch(LuaValue.Nil, LuaValue.Nil, LuaOpcode.Equal, 1), "EQ A=1, equal");

        // LESSTHAN A=0 is Minion's `if MAX < distance then ... end`.
        TestAssert.Equal("else", Branch(N(3000), N(2500), LuaOpcode.LessThan, 0), "LT A=0, left is not smaller");
        TestAssert.Equal("then", Branch(N(1), N(2), LuaOpcode.LessThan, 0), "LT A=0, 1 < 2");
        TestAssert.Equal("else", Branch(N(3000), N(3000), LuaOpcode.LessThan, 0), "LT A=0, equal");

        // Strings order by bytes, numbers by value, and the two never compare equal. These use
        // A=0, the `if a == b then` shape; the A=1 shape is pinned above.
        TestAssert.Equal("then", Branch(S("a"), S("a"), LuaOpcode.Equal, 0), "\"a\" == \"a\"");
        TestAssert.Equal("else", Branch(S("a"), S("b"), LuaOpcode.Equal, 0), "\"a\" == \"b\" is false");
        TestAssert.Equal("then", Branch(S("a"), S("b"), LuaOpcode.LessThan, 0), "\"a\" < \"b\"");
        TestAssert.Equal("else", Branch(S("b"), S("a"), LuaOpcode.LessThan, 0), "\"b\" < \"a\" is false");
        TestAssert.Equal("else", Branch(N(1), S("1"), LuaOpcode.Equal, 0), "1 == \"1\" is false");

        // Tables and functions compare by reference.
        var shared = new LuaAsm();
        shared.NewTable(0);
        shared.Move(1, 0);
        TestAssert.Equal("then", CompareTail(shared, LuaOpcode.Equal, 0), "the same table");

        var two = new LuaAsm();
        two.NewTable(0);
        two.NewTable(1);
        TestAssert.Equal("else", CompareTail(two, LuaOpcode.Equal, 0), "two tables");

        var twoTables = new LuaAsm { MaxStack = 4 };
        twoTables.NewTable(0);
        twoTables.NewTable(1);
        var wrong = ErrorFrom(() => CompareTail(twoTables, LuaOpcode.LessThan, 0));
        TestAssert.Equal("semantics.lua:1: attempt to compare two table values", wrong.Message);
    }

    private static void NumericForRunsInBothDirections()
    {
        TestAssert.Equal(15.0, Number(NumericFor(N(1), N(5), N(1))), "for i = 1, 5 sums 15");
        TestAssert.Equal(6.0, Number(NumericFor(N(3), N(1), N(-1))), "for i = 3, 1, -1 sums 6");
        TestAssert.Equal(0.0, Number(NumericFor(N(1), N(0), N(1))), "an empty forward loop never enters");
        TestAssert.Equal(0.0, Number(NumericFor(N(5), N(1), N(1))), "an empty backward loop never enters");
        TestAssert.Equal(15.0, Number(NumericFor(N(1), S("5"), N(1))), "the limit is coerced like luaV_forlimit");

        // FORPREP insists on numbers; the wording is Lua's own.
        var bad = ErrorFrom(() => Run(NumericFor(N(1), LuaValue.Nil, N(1))));
        TestAssert.Equal("semantics.lua:1: 'for' limit must be a number", bad.Message);
    }

    private static void GenericForUsesTheFollowingJump()
    {
        // for k, v in counter() do sum = sum + k end, assembled exactly as NeutralMinionSpawn
        // assembles `for k, v in pairs(t)`: a JMP over the body into TFORLOOP, and the JMP right
        // after TFORLOOP jumps back to the body.
        var host = NewHost();
        host.Stub("counter", LuaValue.Of(new LuaNative("counter", (state, args) =>
        {
            var limit = args[0].AsNumber;
            var control = args[1].AsNumber;
            if (control >= limit) return new[] { LuaValue.Nil };
            var key = control + 1;
            return new[] { LuaValue.Of(key), LuaValue.Of(key * 10) };
        })), N(3), N(0));

        var loop = new LuaAsm { MaxStack = 8 };
        loop.Load(6, 0);                        // sum = 0
        loop.GetGlobal(0, loop.Ks("counter"));
        loop.Call(0, 1, 4);                     // three results: f, s, control
        loop.Jump(1);                           // straight into TFORLOOP
        loop.Add(6, 6, 3);                      // body: sum = sum + key
        loop.TForLoop(0, 2);                    // two results at R3, R4
        loop.Jump(-3);                          // back to the body
        loop.Return(6, 2);
        TestAssert.Equal(6.0, RunOn(host.State, loop)[0].AsNumber, "sum of the keys 1..3");
    }

    private static void ClosuresShareOpenUpvalues()
    {
        // make() holds `local n = 0` and returns two closures over it.
        var inc = new LuaAsm { Source = "@counter.lua", Upvalues = 1, MaxStack = 4 };
        inc.GetUpval(0, 0);
        inc.Add(0, 0, inc.Knk(1));
        inc.SetUpval(0, 0);
        inc.GetUpval(1, 0);
        inc.Return(1, 2);

        var get = new LuaAsm { Source = "@counter.lua", Upvalues = 1, MaxStack = 4 };
        get.GetUpval(0, 0);
        get.Return(0, 2);

        var make = new LuaAsm { Source = "@counter.lua", MaxStack = 6 };
        make.Load(0, 0);                        // local n = 0
        make.Closure(1, make.AddChild(inc));
        make.CaptureRegister(0);
        make.Closure(2, make.AddChild(get));
        make.CaptureRegister(0);
        make.Return(1, 3);                      // return inc, get

        var main = new LuaAsm { Source = "@counter.lua", MaxStack = 6 };
        main.Closure(0, main.AddChild(make));
        main.Call(0, 1, 3);                     // inc, get
        main.Call(0, 1, 1);
        main.Call(0, 1, 1);
        main.Call(1, 1, 2);                     // get() -> one result
        main.Return(1, 2);
        TestAssert.Equal(2.0, Number(main), "both closures see the same n");
    }

    private static void VarargsAndMultipleResults()
    {
        // local function f(...) return ... end ; return f(1, 2, 3)
        var all = new LuaAsm { Source = "@varargs.lua", IsVararg = 1, MaxStack = 8 };
        all.Vararg(0, 0);
        all.Return(0, 0);
        var main = new LuaAsm { Source = "@varargs.lua", MaxStack = 8 };
        main.Closure(0, main.AddChild(all));
        main.Load(1, 1);
        main.Load(2, 2);
        main.Load(3, 3);
        main.Call(0, 4, 0);                     // three arguments, every result
        main.Return(0, 0);
        TestAssert.Equal(new[] { 1.0, 2.0, 3.0 }, Run(main).Select(v => v.AsNumber).ToArray(), "f(1, 2, 3) passes its varargs through");

        // local function f(a, ...) return a, ... end ; return f(1, 2, 3)
        var leading = new LuaAsm { Source = "@varargs.lua", IsVararg = 1, Params = 1, MaxStack = 8 };
        leading.Vararg(1, 0);
        leading.Return(0, 0);
        var main2 = new LuaAsm { Source = "@varargs.lua", MaxStack = 8 };
        main2.Closure(0, main2.AddChild(leading));
        main2.Load(1, 1);
        main2.Load(2, 2);
        main2.Load(3, 3);
        main2.Call(0, 4, 0);
        main2.Return(0, 0);
        TestAssert.Equal(new[] { 1.0, 2.0, 3.0 }, Run(main2).Select(v => v.AsNumber).ToArray(), "the fixed parameter leads the varargs");

        // VARARG with a fixed count pads with nil.
        var padded = new LuaAsm { Source = "@varargs.lua", IsVararg = 1, MaxStack = 8 };
        padded.Vararg(0, 4);                    // three slots
        padded.Return(0, 4);
        var main3 = new LuaAsm { Source = "@varargs.lua", MaxStack = 8 };
        main3.Closure(0, main3.AddChild(padded));
        main3.Load(1, 9);
        main3.Call(0, 2, 0);
        main3.Return(0, 0);
        var results = Run(main3);
        TestAssert.Equal(3, results.Length, "three vararg slots");
        TestAssert.Equal(9.0, results[0].AsNumber);
        TestAssert.True(results[1].IsNil && results[2].IsNil, "the missing varargs pad with nil");
    }

    private static void TailCallsReuseTheFrame()
    {
        // countdown(n, acc): if n == 0 then return acc end return countdown(n - 1, acc + n)
        var child = new LuaAsm { Source = "@countdown.lua", Params = 2, MaxStack = 8 };
        child.Equal(0, 0, child.Knk(0));
        child.Jump(1);
        child.Return(1, 2);
        child.GetGlobal(3, child.Ks("countdown"));
        child.Add(4, 0, child.Knk(-1));
        child.Add(5, 1, 0);
        child.TailCall(3, 3);
        child.Return(1, 2);

        var main = new LuaAsm { Source = "@countdown.lua", MaxStack = 8 };
        main.Closure(0, main.AddChild(child));
        main.SetGlobal(0, main.Ks("countdown"));
        main.GetGlobal(1, main.Ks("countdown"));
        main.Load(2, 10000);
        main.Load(3, 0);
        main.Call(1, 3, 2);
        main.Return(1, 2);
        // Ten thousand tail calls: the frame is reused, so this must not touch the CLR stack.
        TestAssert.Equal(50005000.0, Number(main), "sum 1..10000 by tail recursion");
    }

    private static void SetListSpillsAcrossItsBlocks()
    {
        // t = {1..50, 51..60}: two SETLIST blocks, the second starting at (2-1)*50 = 50.
        var build = new LuaAsm { Source = "@setlist.lua", MaxStack = 52 };
        build.NewTable(0);
        for (var i = 1; i <= 50; i++) build.Load(i, (double)i);
        build.SetList(0, 50, 1);
        for (var i = 1; i <= 10; i++) build.Load(i, (double)(50 + i));
        build.SetList(0, 10, 2);
        build.Return(0, 2);
        var table = Run(build)[0].AsTable;
        TestAssert.Equal(60, table.Length, "sixty array slots");
        TestAssert.Equal(50.0, table.Get(N(50)).AsNumber, "the end of the first block");
        TestAssert.Equal(51.0, table.Get(N(51)).AsNumber, "the start of the second block");
        TestAssert.Equal(60.0, table.Get(N(60)).AsNumber, "the end of the second block");

        // C = 0 moves the block number into the next instruction word.
        var extended = new LuaAsm { Source = "@setlist.lua", MaxStack = 8 };
        extended.NewTable(0);
        extended.Load(1, 10);
        extended.Load(2, 20);
        extended.SetList(0, 2, 0);
        extended.Word(2);
        extended.Return(0, 2);
        var spill = Run(extended)[0].AsTable;
        TestAssert.Equal(10.0, spill.Get(N(51)).AsNumber, "the extension word names the block");
        TestAssert.Equal(20.0, spill.Get(N(52)).AsNumber);

        // B = 0 takes the values up to the top, which is what a multret call leaves behind.
        var host = NewHost();
        host.Stub("three", N(1), N(2), N(3));
        var multi = new LuaAsm { Source = "@setlist.lua", MaxStack = 8 };
        multi.NewTable(0);
        multi.GetGlobal(1, multi.Ks("three"));
        multi.Call(1, 1, 0);
        multi.SetList(0, 0, 1);
        multi.Return(0, 2);
        var gathered = RunOn(host.State, multi)[0].AsTable;
        TestAssert.Equal(3, gathered.Length, "a multret SETLIST fills the table");
        TestAssert.Equal(3.0, gathered.Get(N(3)).AsNumber);
    }

    private static void MetatablesSupplyMissingOperations()
    {
        var meta = new LuaTable();
        meta.Set(LuaValue.Of("__index"), new LuaNative("__index", (state, args) => new[] { S("from-meta") }));
        meta.Set(LuaValue.Of("__newindex"), new LuaNative("__newindex", (state, args) =>
        {
            state.SetGlobal("stored", args[2]);
            return Array.Empty<LuaValue>();
        }));
        meta.Set(LuaValue.Of("__add"), new LuaNative("__add", (state, args) => new[] { N(42) }));
        meta.Set(LuaValue.Of("__concat"), new LuaNative("__concat", (state, args) => new[] { S("joined") }));
        meta.Set(LuaValue.Of("__call"), new LuaNative("__call", (state, args) => new[] { args[0], args[1], S("called") }));
        meta.Set(LuaValue.Of("__len"), new LuaNative("__len", (state, args) => new[] { N(7) }));
        meta.Set(LuaValue.Of("__tostring"), new LuaNative("__tostring", (state, args) => new[] { S("shown") }));
        meta.Set(LuaValue.Of("__eq"), new LuaNative("__eq", (state, args) => new[] { LuaValue.Of(true) }));
        meta.Set(LuaValue.Of("__lt"), new LuaNative("__lt", (state, args) => new[] { LuaValue.Of(false) }));

        LuaValue Target() => LuaValue.Of(new LuaTable { Metatable = meta });

        var host = NewHost();
        host.State.SetGlobal("t", Target());

        // t.missing reads through __index.
        var index = new LuaAsm { Source = "@meta.lua", MaxStack = 8 };
        index.GetGlobal(0, index.Ks("t"));
        index.GetTable(1, 0, index.Ksk("missing"));
        index.Return(1, 2);
        TestAssert.Equal("from-meta", RunOn(host.State, index)[0].AsString, "__index");

        // t.missing = 5 writes through __newindex.
        var newIndex = new LuaAsm { Source = "@meta.lua", MaxStack = 8 };
        newIndex.GetGlobal(0, newIndex.Ks("t"));
        newIndex.Load(1, 5);
        newIndex.SetTable(0, newIndex.Ksk("missing"), 1);
        newIndex.Return(0, 1);
        var placed = RunOn(host.State, newIndex);
        TestAssert.Equal(0, placed.Length, "__newindex replaces the write");
        TestAssert.Equal(5.0, host.State.GetGlobal("stored").AsNumber, "the value reached __newindex");

        // t + 1 and t .. 'x'.
        var add = new LuaAsm { Source = "@meta.lua", MaxStack = 8 };
        add.GetGlobal(0, add.Ks("t"));
        add.Load(1, 1);
        add.Add(2, 0, 1);
        add.Return(2, 2);
        TestAssert.Equal(42.0, RunOn(host.State, add)[0].AsNumber, "__add");

        var concat = new LuaAsm { Source = "@meta.lua", MaxStack = 8 };
        concat.GetGlobal(0, concat.Ks("t"));
        concat.Load(1, "x");
        concat.Concat(2, 0, 1);
        concat.Return(2, 2);
        TestAssert.Equal("joined", RunOn(host.State, concat)[0].AsString, "__concat");

        // t(7) calls the table, with the table itself as the first argument.
        var call = new LuaAsm { Source = "@meta.lua", MaxStack = 8 };
        call.GetGlobal(0, call.Ks("t"));
        call.Load(1, 7);
        call.Call(0, 2, 4);
        call.Return(0, 4);
        var results = RunOn(host.State, call);
        TestAssert.Equal(3, results.Length, "__call produces three results");
        TestAssert.Equal(7.0, results[1].AsNumber, "the argument follows the called value");
        TestAssert.Equal("called", results[2].AsString);

        // #t and tostring(t).
        var length = new LuaAsm { Source = "@meta.lua", MaxStack = 8 };
        length.GetGlobal(0, length.Ks("t"));
        length.Length(1, 0);
        length.Return(1, 2);
        TestAssert.Equal(7.0, RunOn(host.State, length)[0].AsNumber, "__len");

        var show = new LuaAsm { Source = "@meta.lua", MaxStack = 8 };
        show.GetGlobal(0, show.Ks("tostring"));
        show.GetGlobal(1, show.Ks("t"));
        show.Call(0, 2, 2);
        show.Return(0, 2);
        TestAssert.Equal("shown", RunOn(host.State, show)[0].AsString, "__tostring");

        // __eq fires for two non-raw-equal tables sharing the metamethod.
        host.State.SetGlobal("u", Target());
        var equal = new LuaAsm { Source = "@meta.lua", MaxStack = 8 };
        equal.GetGlobal(0, equal.Ks("t"));
        equal.GetGlobal(1, equal.Ks("u"));
        equal.Equal(0, 0, 1);
        equal.Jump(2);
        equal.Load(2, "then");
        equal.Return(2, 2);
        equal.Load(3, "else");
        equal.Return(3, 2);
        TestAssert.Equal("then", RunOn(host.State, equal)[0].AsString, "__eq");

        // __lt replaces the order; it answers false, so the else-arm wins.
        var less = new LuaAsm { Source = "@meta.lua", MaxStack = 8 };
        less.GetGlobal(0, less.Ks("t"));
        less.GetGlobal(1, less.Ks("u"));
        TestAssert.Equal("else", CompareTail(host.State, less, LuaOpcode.LessThan, 0), "__lt");
    }

    private static void ErrorsCarryTheChunkAndLine()
    {
        var call = new LuaAsm { Source = "@boom.lua", Line = 3 };
        call.LoadNil(0, 0);
        call.Call(0, 1, 1);
        call.Return(0, 1);
        TestAssert.Equal("boom.lua:3: attempt to call a nil value", ErrorFrom(() => Run(call)).Message);

        var index = new LuaAsm { Source = "@boom.lua", Line = 5 };
        index.LoadNil(0, 0);
        index.GetTable(1, 0, index.Ksk("x"));
        index.Return(1, 2);
        TestAssert.Equal("boom.lua:5: attempt to index a nil value", ErrorFrom(() => Run(index)).Message);

        var length = new LuaAsm { Source = "@boom.lua", Line = 7 };
        length.LoadNil(0, 0);
        length.Length(1, 0);
        length.Return(1, 2);
        TestAssert.Equal("boom.lua:7: attempt to get length of a nil value", ErrorFrom(() => Run(length)).Message);

        var indexNil = new LuaAsm { Source = "@boom.lua", Line = 9 };
        indexNil.NewTable(0);
        indexNil.SetTable(0, indexNil.Knilk(), indexNil.Knk(1));
        indexNil.Return(0, 1);
        TestAssert.Equal("boom.lua:9: table index is nil", ErrorFrom(() => Run(indexNil)).Message);

        // error() prefixes its own position and is not prefixed again by the interpreter.
        var raise = new LuaAsm { Source = "@boom.lua", Line = 11, MaxStack = 8 };
        raise.GetGlobal(0, raise.Ks("error"));
        raise.Load(1, "bang");
        raise.Call(0, 2, 1);
        raise.Return(0, 1);
        TestAssert.Equal("boom.lua:11: bang", ErrorFrom(() => Run(raise)).Message);

        // pcall turns a script error into a false result and the error value.
        var guard = new LuaAsm { Source = "@guard.lua", MaxStack = 8 };
        guard.GetGlobal(0, guard.Ks("pcall"));
        guard.Closure(1, guard.AddChild(raise));
        guard.Call(0, 2, 3);
        guard.Return(0, 3);
        var recovered = Run(guard);
        TestAssert.Equal(2, recovered.Length, "pcall returns its status and the value");
        TestAssert.Equal(false, recovered[0].AsBoolean, "pcall status");
        TestAssert.Equal("boom.lua:11: bang", recovered[1].AsString, "pcall passes the located error through");
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static LuaRecordingHost NewHost() => new(GameDataPaths.Default);

    private static LuaValue N(double value) => LuaValue.Of(value);

    private static LuaValue S(string value) => LuaValue.Of(value);

    private static LuaValue[] Run(LuaAsm assembly) => RunOn(NewState(), assembly);

    private static LuaValue[] RunOn(LuaState state, LuaAsm assembly) => state.DoChunk(assembly.Chunk());

    private static LuaState NewState()
    {
        var state = new LuaState();
        state.OpenLibs();
        return state;
    }

    private static double Number(LuaAsm assembly) => Run(assembly)[0].AsNumber;

    /// <summary>Builds <c>R2 = R0 op R1</c> over two values and returns the single result.</summary>
    private static LuaAsm Bin(LuaOpcode op, LuaValue left, LuaValue right)
    {
        var asm = new LuaAsm();
        Put(asm, 0, left);
        Put(asm, 1, right);
        asm.Binary(op, 2, 0, 1);
        asm.Return(2, 2);
        return asm;
    }

    private static string Concat(LuaValue left, LuaValue right) => Run(ConcatAssembly(left, right))[0].AsString;

    private static LuaAsm ConcatAssembly(LuaValue left, LuaValue right)
    {
        var asm = new LuaAsm();
        Put(asm, 0, left);
        Put(asm, 1, right);
        asm.Concat(2, 0, 1);
        asm.Return(2, 2);
        return asm;
    }

    /// <summary>
    /// <c>if a op b then return "then" end return "else"</c> with a comparison carrying the given
    /// A flag, so the check pins which side of the JMP each outcome lands on.
    /// </summary>
    private static string Branch(LuaValue left, LuaValue right, LuaOpcode op, int flag) =>
        CompareTail(Prepared(left, right), op, flag);

    /// <summary>The same arm, appended to a prototype whose R0 and R1 already hold the operands.</summary>
    private static string CompareTail(LuaAsm asm, LuaOpcode op, int flag) =>
        CompareTail(NewState(), asm, op, flag);

    /// <summary>The same arm, run on a state that already holds the operands' globals.</summary>
    private static string CompareTail(LuaState state, LuaAsm asm, LuaOpcode op, int flag)
    {
        asm.Binary(op, flag, 0, 1);
        asm.Jump(2);
        asm.Load(2, "then");
        asm.Return(2, 2);
        asm.Load(3, "else");
        asm.Return(3, 2);
        return RunOn(state, asm)[0].AsString;
    }

    private static LuaAsm Prepared(LuaValue left, LuaValue right)
    {
        var asm = new LuaAsm();
        Put(asm, 0, left);
        Put(asm, 1, right);
        return asm;
    }

    /// <summary><c>for i = init, limit, step do sum = sum + i end</c>, returning <c>sum</c>.</summary>
    private static LuaAsm NumericFor(LuaValue init, LuaValue limit, LuaValue step)
    {
        // R0..R2 are the control values, R3 is the visible loop variable (the copy FORLOOP makes
        // at R(A+3)), so the accumulator lives at R4 and adds R3, not the internal index.
        var asm = new LuaAsm { MaxStack = 8 };
        Put(asm, 0, init);
        Put(asm, 1, limit);
        Put(asm, 2, step);
        asm.Load(4, 0);
        // FORPREP, the body, FORLOOP, RETURN: sBx counts from the instruction after the jump, so
        // FORPREP steps forward over the body onto FORLOOP and FORLOOP steps back onto the body.
        asm.ForPrep(0, 1);
        asm.Add(4, 4, 3);
        asm.ForLoop(0, -2);
        asm.Return(4, 2);
        return asm;
    }

    /// <summary>
    /// Loads a value into a register with the opcode Lua would pick. Tables and functions have no
    /// literal form, so tests that need one build it with <see cref="LuaAsm.NewTable"/> or read it
    /// from a host global.
    /// </summary>
    private static void Put(LuaAsm asm, int register, LuaValue value)
    {
        switch (value.Type)
        {
            case LuaType.Number: asm.Load(register, value.AsNumber); break;
            case LuaType.String: asm.Load(register, value.AsString); break;
            case LuaType.Boolean: asm.Load(register, value.AsBoolean); break;
            case LuaType.Nil: asm.LoadNil(register, register); break;
            default:
                throw new InvalidOperationException($"a {value.TypeName()} has no literal form");
        }
    }

    private static LuaError ErrorFrom(Action action)
    {
        try
        {
            action();
        }
        catch (LuaError error)
        {
            return error;
        }
        throw new InvalidOperationException("Expected a LuaError.");
    }
}
