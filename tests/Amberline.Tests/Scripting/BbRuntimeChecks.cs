using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Tests;

/// <summary>Block-building helpers for the interpreter checks.</summary>
internal static class BbTest
{
    public static BbValue Ref(string name) => new BbValue.Reference(name);

    private static BbValue Value(object value) => value switch
    {
        BbValue shipped => shipped,
        bool b => new BbValue.Bool(b),
        int i => new BbValue.Number(i),
        double d => new BbValue.Number(d),
        string s => new BbValue.Text(s),
        _ => throw new ArgumentException($"unsupported test value {value}"),
    };

    /// <summary>A block; <paramref name="parameters"/> alternates key and value.</summary>
    public static BbBlock Block(string function, object[] parameters, params BbBlock[] subBlocks)
    {
        var fields = new List<BbField>();
        for (var i = 0; i < parameters.Length; i += 2) fields.Add(new BbField((string)parameters[i], Value(parameters[i + 1])));
        return new BbBlock(function, fields, subBlocks);
    }

    public static BbBlock SetVar(string name, object value, string? table = null) =>
        table is null ? Block("BBSetVarInTable", ["DestVar", name, "SrcValue", value])
                      : Block("BBSetVarInTable", ["DestVar", name, "DestVarTable", table, "SrcValue", value]);

    /// <summary><c>name = name + 1</c> in the root scope, the checks' execution counter.</summary>
    public static BbBlock Increment(string name) =>
        Block("BBMath", ["Src1Var", name, "Src1Value", 0, "Src2Value", 1, "DestVar", name, "MathOp", Ref("MO_ADD")]);

    public static BbBlock If(string variable, string op, object value, params BbBlock[] body) =>
        Block("BBIf", ["Src1Var", variable, "Value2", value, "CompareOp", Ref(op)], body);

    public sealed class Run
    {
        public Run(IReadOnlyList<BbBlock> blocks, double? level = null, Action<BbScope>? setup = null)
        {
            Core = new OracleCore();
            Interpreter = new BbInterpreter(new OracleHost(Core));
            Scope = BbScope.ForHook(OracleCore.Owner, OracleCore.Attacker, OracleCore.Target, OracleCore.Caster, level);
            setup?.Invoke(Scope);
            Interpreter.Execute(blocks, Scope);
        }

        public OracleCore Core { get; }
        public BbInterpreter Interpreter { get; }
        public BbScope Scope { get; }
        public object? Var(string name) => Scope.Vars[name];
    }
}

/// <summary>Milestone M1 / P1 of the BuildingBlocks engine: the pure interpreter and the base library.</summary>
public static class BbRuntimeChecks
{
    private static GameDataPaths Paths => GameDataPaths.Default;

    [Check("scripting.bb-base-library-matches-shipped-file")]
    public static void BaseLibraryCoversEveryFunctionTheBaseFileDefines()
    {
        // Every global BuildingBlocksBase.luaobj binds to a closure must have a C# port, and the port must hold nothing else.
        var chunk = LuaObjectReader.ParseData(Paths, "BuildingBlocks/BuildingBlocksBase.luaobj").TopLevel;
        TestAssert.Equal(92, chunk.Prototypes.Count, "functions the base file defines");
        var defined = new List<string>();
        for (var pc = 1; pc < chunk.Code.Count; pc++)
        {
            var store = LuaInstruction.Decode(chunk.Code[pc]);
            if (store.Opcode != (int)LuaOpcode.SetGlobal || LuaInstruction.Decode(chunk.Code[pc - 1]).Opcode != (int)LuaOpcode.Closure) continue;
            defined.Add(((LuaConstant.String)chunk.Constants[store.Bx]).Value);
        }
        TestAssert.Equal(92, defined.Count, "closures bound to globals");

        var ported = BbBaseLibrary.BlockFunctionNames
            .Concat(Enum.GetNames<BbComparison>()).Concat(Enum.GetNames<BbMathOp>())
            .Concat(["ExecuteBuildingBlocks", "GetTable", "GetMathNumber", "GetNumber"])
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();
        TestAssert.Equal(defined.OrderBy(n => n, StringComparer.Ordinal).ToArray(), ported, "ported functions vs the base file's globals");
        TestAssert.Equal(52, BbBaseLibrary.BlockFunctionNames.Count, "block functions");
    }

    [Check("scripting.bb-constants-cover-corpus")]
    public static void EveryShippedReferenceResolves()
    {
        var resolver = new BbScriptResolver(Paths);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var relative in ScriptFiles())
        {
            var result = resolver.Load(relative);
            if (result.Script is null) continue;
            foreach (var (_, block) in result.Script.Walk())
                foreach (var field in block.Params) CollectReferences(field.Value, names);
            foreach (var global in result.Script.Globals) CollectReferences(global.Value, names);
        }
        TestAssert.True(names.Count >= 250, $"expected at least 250 distinct references, saw {names.Count}");

        var unresolved = names.Where(n => !BbConstants.TryGetValue(n, out _) && !BbRuntime.IsHostFunction(n)).ToArray();
        // Names the stock client never defines: Lua reads them as nil.
        TestAssert.Equal(new[] { "False", "NoTargetYet", "NoValidTarget", "True" }, unresolved, "unresolved references: " + string.Join(",", unresolved));
        TestAssert.Equal(BbConstants.UndefinedInRetail.OrderBy(n => n, StringComparer.Ordinal).ToArray(), unresolved);
        foreach (var name in unresolved) TestAssert.True(BbRuntime.ResolveReference(name) is null, name + " reads as nil");

        // Retail registrations (the client's table): aliases stay equal, the unsigned sentinel survives.
        TestAssert.Equal(100.0, BbConstants.All["TEAM_ORDER"]);
        TestAssert.Equal(200.0, BbConstants.All["TEAM_CHAOS"]);
        TestAssert.Equal(1.0, BbConstants.All["MAGIC_DAMAGE"]);
        TestAssert.Equal(4.0, BbConstants.All["BUFF_Stun"]);
        TestAssert.Equal(BbConstants.All["BUFF_Silence"], BbConstants.All["BUFF_Pacified"]);
        TestAssert.Equal(BbConstants.All["BUFF_Silence"], BbConstants.All["BUFF_Disarm"]);
        TestAssert.Equal(4294967295.0, BbConstants.All["TTYPE_Invalid"]);
        TestAssert.Equal(3.0, BbConstants.All["HIT_Miss"]);
        TestAssert.Equal(2.0, BbConstants.All["HIT_Dodge"]);
        TestAssert.Equal((int)BbDamageType.TRUE_DAMAGE, (int)BbConstants.All["TRUE_DAMAGE"]);
        TestAssert.Equal(0x400u, (uint)BbSpellFlags.UNITSCAN_Enemies);
        TestAssert.Equal(164, BbConstants.All.Count, "registered constants");
    }

    [Check("scripting.bb-native-routing")]
    public static void NativesRouteToTheirOwnersHandler()
    {
        // Every function the corpus uses as a block is either a base function or a catalogued native.
        var resolver = new BbScriptResolver(Paths);
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var relative in ScriptFiles())
        {
            var script = resolver.Load(relative).Script;
            if (script is null) continue;
            foreach (var (_, block) in script.Walk()) used.Add(block.Function);
        }
        TestAssert.Equal(179, used.Count, "distinct block functions");
        var natives = used.Where(n => !BbBaseLibrary.TryGet(n, out _)).ToArray();
        TestAssert.Equal(134, natives.Length, "native block functions");
        TestAssert.Equal(natives, BbNativeCatalog.Names.OrderBy(n => n, StringComparer.Ordinal).ToArray(), "catalog vs corpus natives");
        TestAssert.Equal(0, natives.Count(n => BbNativeCatalog.OwnerOf(n) is null));

        var handlers = new Dictionary<BbNativeOwner, RecordingHandler>();
        foreach (var owner in Enum.GetValues<BbNativeOwner>()) handlers[owner] = new RecordingHandler();
        var host = new RoutingHost(handlers);
        var interpreter = new BbInterpreter(host);
        var scope = BbScope.ForHook();
        foreach (var native in natives) interpreter.Execute([BbTest.Block(native, ["Probe", native])], scope);
        foreach (var owner in Enum.GetValues<BbNativeOwner>())
        {
            var expected = natives.Where(n => BbNativeCatalog.OwnerOf(n) == owner).ToArray();
            TestAssert.True(expected.Length > 0, owner + " owns natives");
            TestAssert.Equal(expected, handlers[owner].Calls.OrderBy(n => n, StringComparer.Ordinal).ToArray(), owner + " handler calls");
        }

        // A host that binds no handler, and a function nobody defines, fail like Lua calling nil.
        var bare = new BbInterpreter(new RoutingHost(new Dictionary<BbNativeOwner, RecordingHandler>()));
        TestAssert.Throws<BbRuntimeException>(() => bare.Execute([BbTest.Block("BBApplyDamage", [])], BbScope.ForHook()));
        TestAssert.Throws<BbRuntimeException>(() => interpreter.Execute([BbTest.Block("BBNoSuchFunction", [])], BbScope.ForHook()));
        // Natives see the block they were called with, and their sub-blocks run in the same scope.
        var scoped = new Dictionary<BbNativeOwner, RecordingHandler> { [BbNativeOwner.Queries] = new RecordingHandler { RunSubBlocks = true } };
        var run = new BbInterpreter(new RoutingHost(scoped));
        var s2 = BbScope.ForHook();
        run.Execute([BbTest.Block("BBForEachUnitInTargetArea", ["IteratorVar", "Unit"], BbTest.SetVar("inside", 7))], s2);
        TestAssert.Equal(7.0, s2.Vars["inside"]);
    }

    [Check("scripting.bb-get-table-fails-at-the-callers-index-not-before")]
    public static void GetTableFailsAtTheCallersIndex()
    {
        // BuildingBlocksBase.luaobj: GetTable (proto 0.3) returns scope[name] when it is not nil, a table or not, and BBSetVarInTable
        // indexes the source table only when SrcVar is given (proto 0.4 reads SrcValue otherwise). A SrcVarTable that names a unit with no
        // SrcVar therefore runs; with a SrcVar the index fails, with Lua's message.
        var literal = new BbTest.Run([BbTest.Block("BBSetVarInTable", ["DestVar", "out", "SrcVarTable", "Owner", "SrcValue", 7])]);
        TestAssert.Equal(7.0, literal.Var("out"), "no SrcVar: the literal is copied and the unit is never indexed");
        string? message = null;
        try { _ = new BbTest.Run([BbTest.Block("BBSetVarInTable", ["DestVar", "out", "SrcVarTable", "Owner", "SrcVar", "x"])]); }
        catch (BbRuntimeException ex) { message = ex.Message; }
        TestAssert.True(message?.Contains("attempt to index a", StringComparison.Ordinal) == true, $"a SrcVar indexes the unit and fails: {message}");
    }

    [Check("scripting.bb-scope-semantics")]
    public static void ScopeTablesFollowLuaSemantics()
    {
        var table = new BbTable();
        TestAssert.True(table["missing"] is null && table[null] is null, "nil and unknown keys read nil");
        TestAssert.Throws<BbRuntimeException>(() => table[null] = 1.0, "writing a nil key raises");
        TestAssert.Throws<BbRuntimeException>(() => table[double.NaN] = 1.0, "writing a NaN key raises");
        table[1] = "int key";
        TestAssert.Equal("int key", table[1.0] as string, "int and double keys are the same number");
        table["a"] = 2.0;
        table["a"] = null;
        TestAssert.Equal(1, table.Count, "writing nil removes");

        var scope = BbScope.ForHook(OracleCore.Owner, OracleCore.Attacker, OracleCore.Target, OracleCore.Caster, 3);
        TestAssert.True(scope.InstanceVars is not null && scope.NextBuffVars is not null && scope.CharVars is not null &&
                        scope.SpellVars is not null && scope.AvatarVars is not null, "the five variable tables exist");
        TestAssert.Equal(OracleCore.Owner, (OracleUnit)scope.Owner!);
        TestAssert.True(ReferenceEquals(scope.Nothing, BbScope.NothingUnit), "Nothing is the sentinel");
        TestAssert.Equal(3.0, scope.Level);
        TestAssert.True(scope.Unit is null && scope.Other1 is null, "unfilled slots are nil");

        // GetTable: no name, a missing name without create, and a missing name with create.
        TestAssert.True(ReferenceEquals(BbBaseLibrary.GetTable(scope, null, true), scope.Vars), "no name is the root scope");
        TestAssert.True(ReferenceEquals(BbBaseLibrary.GetTable(scope, "Nope", false), scope.Vars), "missing without create is the root scope");
        TestAssert.True(scope.Vars["Nope"] is null, "and creates nothing");
        var made = BbBaseLibrary.GetTable(scope, "Nope", true);
        TestAssert.True(!ReferenceEquals(made, scope.Vars) && ReferenceEquals(scope.Vars["Nope"], made), "create stores a fresh table");
        TestAssert.True(ReferenceEquals(BbBaseLibrary.GetTable(scope, "Nope", false), made), "an existing table is returned either way");
        // Batch 241: GetTable hands back the variable whatever it holds (BuildingBlocksBase.luaobj proto 0.3); the caller's own index fails.
        var notTable = BbBaseLibrary.GetTable(scope, "Owner", false);
        TestAssert.Throws<BbRuntimeException>(() => _ = notTable["x"], "a unit is not a table: indexing it fails");

        // BBSetVarInTable: root, named table (created on demand), from another variable, and the literal.
        var run = new BbTest.Run([
            BbTest.SetVar("plain", 1),
            BbTest.SetVar("inst", 2, "InstanceVars"),
            BbTest.SetVar("fresh", 3, "BrandNewVars"),
            BbTest.Block("BBSetVarInTable", ["DestVar", "copy", "DestVarTable", "NextBuffVars", "SrcVar", "inst", "SrcVarTable", "InstanceVars"]),
            BbTest.Block("BBSetVarInTable", ["DestVar", "rootCopy", "SrcVar", "plain"]),
            BbTest.Block("BBSetVarInTable", ["DestVar", "gone", "SrcValue", BbTest.Ref("NoValidTarget")]),
            BbTest.Block("BBSetVarInTable", ["DestVar", "team", "SrcValue", BbTest.Ref("TEAM_CHAOS")]),
        ]);
        TestAssert.Equal(1.0, run.Var("plain"));
        TestAssert.Equal(2.0, run.Scope.InstanceVars!["inst"]);
        TestAssert.Equal(3.0, ((BbTable)run.Var("BrandNewVars")!)["fresh"]);
        TestAssert.Equal(2.0, run.Scope.NextBuffVars!["copy"]);
        TestAssert.Equal(1.0, run.Var("rootCopy"));
        TestAssert.True(run.Var("gone") is null, "an undefined global reads nil, so the variable is unset");
        TestAssert.Equal(200.0, run.Var("team"));
        TestAssert.Equal(false, run.Var("LastIfSucceeded") is not null, "no If ran, so LastIfSucceeded stays nil");
        TestAssert.Equal(1L, new BbTest.Run([BbTest.Block("BBBreakExecution", []), BbTest.SetVar("x", 1)]).Interpreter.BlocksExecuted, "the block after a break never starts");

        // The pseudo-globals end where ExecuteBuildingBlocks leaves them, and a failure names its block path.
        TestAssert.Equal("", run.Interpreter.CurrentBlockString);
        TestAssert.Equal(-1, run.Interpreter.CurrentBlockNumber);
        TestAssert.True(run.Interpreter.CurrentBlock is null, "gCurrentBuildingBlock is cleared");
        var failing = new BbInterpreter(new OracleHost(new OracleCore()));
        var failure = Catch<BbRuntimeException>(() => failing.Execute(
            [BbTest.SetVar("ok", 1), BbTest.If("ok", "CO_EQUAL", 1, BbTest.Block("BBMath", ["Src1Var", "unset", "Src2Value", 1, "DestVar", "r", "MathOp", BbTest.Ref("MO_ADD")]))],
            BbScope.ForHook()));
        TestAssert.True(failure.BlockPath == ".2.1", "block path of the failing block was " + failure.BlockPath);
        TestAssert.True(failure.Message.Contains("nil value", StringComparison.Ordinal), failure.Message);
    }

    [Check("scripting.bb-break-execution")]
    public static void BreakExecutionLikeTheShippedFlagProtocol()
    {
        // BBBreakExecution stops the rest of the list it runs in.
        var run = new BbTest.Run([BbTest.SetVar("a", 1), BbTest.Block("BBBreakExecution", []), BbTest.SetVar("b", 1)]);
        TestAssert.Equal(1.0, run.Var("a"));
        TestAssert.True(run.Var("b") is null, "blocks after the break do not run");

        // Inside an If it stops the enclosing lists too, because the flag is shared and every list tests it after each block.
        run = new BbTest.Run([
            BbTest.SetVar("cond", 1),
            BbTest.If("cond", "CO_EQUAL", 1, BbTest.SetVar("in", 1), BbTest.Block("BBBreakExecution", []), BbTest.SetVar("skipped", 1)),
            BbTest.SetVar("after", 1),
        ]);
        TestAssert.Equal(1.0, run.Var("in"));
        TestAssert.True(run.Var("skipped") is null && run.Var("after") is null, "the outer list stops as well");

        // A false If never reaches its body, so the break never happens.
        run = new BbTest.Run([BbTest.SetVar("cond", 2), BbTest.If("cond", "CO_EQUAL", 1, BbTest.Block("BBBreakExecution", [])), BbTest.SetVar("after", 1)]);
        TestAssert.Equal(1.0, run.Var("after"));

        // Every ExecuteBuildingBlocks call resets the flag first: a While body that breaks is re-entered, and the
        // flag the last iteration leaves behind stops the list that holds the While.
        run = new BbTest.Run([
            BbTest.SetVar("i", 0),
            BbTest.Block("BBWhile", ["Src1Var", "i", "Value2", 3, "CompareOp", BbTest.Ref("CO_LESS_THAN")],
                BbTest.Increment("i"), BbTest.Block("BBBreakExecution", []), BbTest.Increment("never")),
            BbTest.SetVar("after", 1),
        ]);
        TestAssert.Equal(3.0, run.Var("i"));
        TestAssert.True(run.Var("never") is null && run.Var("after") is null, "break leaves the While body, not the While");

        // The scope's flag is reset by the next top-level call.
        var scope = BbScope.ForHook();
        var interpreter = new BbInterpreter(new OracleHost(new OracleCore()));
        interpreter.Execute([BbTest.Block("BBBreakExecution", [])], scope);
        TestAssert.Equal(true, scope.Vars[BbScope.BreakExecutionKey]);
        interpreter.Execute([BbTest.SetVar("again", 1)], scope);
        TestAssert.Equal(1.0, scope.Vars["again"]);
        interpreter.Execute(null, scope);
        TestAssert.Equal(false, scope.Vars[BbScope.BreakExecutionKey], "a null list does nothing, so the flag stays as the last list left it");
    }

    [Check("scripting.bb-if-else-chains")]
    public static void IfElseIfElseFollowSiblingOrder()
    {
        BbBlock[] Chain(object x) =>
        [
            BbTest.SetVar("x", x),
            BbTest.If("x", "CO_EQUAL", 1, BbTest.SetVar("branch", "if")),
            BbTest.Block("BBElseIf", ["Src1Var", "x", "Value2", 2, "CompareOp", BbTest.Ref("CO_EQUAL")], BbTest.SetVar("branch", "elseif")),
            BbTest.Block("BBElse", [], BbTest.SetVar("branch", "else")),
        ];
        TestAssert.Equal("if", new BbTest.Run(Chain(1)).Var("branch"));
        TestAssert.Equal("elseif", new BbTest.Run(Chain(2)).Var("branch"));
        TestAssert.Equal("else", new BbTest.Run(Chain(3)).Var("branch"));
        // BBElse records success, so a second Else after it does nothing; the flag is shared by every If at every depth.
        var run = new BbTest.Run([
            BbTest.If("missing", "CO_EQUAL", BbTest.Ref("NoValidTarget"), BbTest.SetVar("one", 1)),
            BbTest.Block("BBElse", [], BbTest.SetVar("else1", 1)),
            BbTest.Block("BBElse", [], BbTest.SetVar("else2", 1)),
        ]);
        TestAssert.Equal(1.0, run.Var("one"));
        TestAssert.True(run.Var("else1") is null && run.Var("else2") is null, "an undefined global is nil, so nil == nil took the If");
        run = new BbTest.Run([
            BbTest.SetVar("x", 1),
            BbTest.If("x", "CO_EQUAL", 1, BbTest.If("x", "CO_EQUAL", 2, BbTest.SetVar("inner", 1))),
            BbTest.Block("BBElse", [], BbTest.SetVar("else", 1)),
        ]);
        TestAssert.True(run.Var("inner") is null && run.Var("else") is null, "the outer If succeeded after its body, whatever the inner one did");
        TestAssert.Equal(true, run.Var("LastIfSucceeded"));
        // An Else with no If before it (LastIfSucceeded nil) is a no-op.
        TestAssert.True(new BbTest.Run([BbTest.Block("BBElse", [], BbTest.SetVar("lonely", 1))]).Var("lonely") is null);
    }

    [Check("scripting.bb-bylevel-indexing")]
    public static void ByLevelTablesIndexByTheSpellRank()
    {
        var table = new BbValue.Table([new BbValue.Number(85), new BbValue.Number(125), new BbValue.Number(165), new BbValue.Number(205), new BbValue.Number(245)], []);
        var setVar = BbTest.Block("BBSetVarInTable", ["DestVar", "d", "SrcValueByLevel", table, "SrcValue", -1]);
        for (var rank = 1; rank <= 5; rank++)
            TestAssert.Equal(table.Items.Select(i => ((BbValue.Number)i).Value).ElementAt(rank - 1), new BbTest.Run([setVar], rank).Var("d"), "rank " + rank);
        TestAssert.True(new BbTest.Run([setVar], 0).Var("d") is null, "rank 0 is nil, which unsets the variable");
        TestAssert.True(new BbTest.Run([setVar], 6).Var("d") is null, "rank 6 is past the table");
        TestAssert.True(new BbTest.Run([setVar], 2.5).Var("d") is null, "a fractional rank is no key");
        TestAssert.Equal(-1.0, new BbTest.Run([setVar]).Var("d"), "without Level the literal is used");

        var returned = BbTest.Block("BBSetReturnValue", ["SrcValueByLevel", table]);
        TestAssert.Equal(165.0, new BbTest.Run([returned], 3).Scope.ReturnValue);

        // BBIncStat adds Delta, DeltaByLevel[Level] and the DeltaVar variable, then calls the accessor.
        var inc = BbTest.Block("BBIncStat", ["Stat", BbTest.Ref("IncFlatArmorMod"), "TargetVar", "Owner", "Delta", 1, "DeltaByLevel", table, "DeltaVar", "bonus", "DeltaVarTable", "InstanceVars"]);
        var withBonus = new BbTest.Run([BbTest.SetVar("bonus", 10, "InstanceVars"), inc], 2);
        TestAssert.Equal("IncFlatArmorMod(136, Owner)", withBonus.Core.Log.Single());
        TestAssert.Throws<BbRuntimeException>(() => new BbTest.Run([BbTest.SetVar("bonus", 10, "InstanceVars"), inc], 9), "a rank past the table adds nil");
        // Without TargetVar the accessor is called without the unit argument.
        var noTarget = BbTest.Block("BBIncStat", ["Stat", BbTest.Ref("IncFlatArmorMod"), "Delta", 4]);
        TestAssert.Equal("IncFlatArmorMod(4)", new BbTest.Run([noTarget]).Core.Log.Single());

        // The same table in the shipped Annie closure: each rank reads its own entry.
        var closure = new BbScriptResolver(Paths).Resolve("Annie");
        var byLevel = closure.Scripts.SelectMany(s => s.Script.Walk()).Where(w => w.Block.Function == "BBSetVarInTable" && w.Block.Param("SrcValueByLevel") is BbValue.Table).ToArray();
        TestAssert.True(byLevel.Length > 0, "Annie's scripts use SrcValueByLevel");
        foreach (var (_, block) in byLevel)
        {
            var items = ((BbValue.Table)block.Param("SrcValueByLevel")!).Items.Select(i => ((BbValue.Number)i).Value).ToArray();
            for (var rank = 1; rank <= items.Length; rank++)
            {
                var one = new BbTest.Run([block], rank);
                var target = block.Text("DestVarTable") is { } named ? ((BbTable)one.Var(named)!) : one.Scope.Vars;
                TestAssert.Equal(items[rank - 1], target[block.Text("DestVar")], $"{block.Text("DestVar")} at rank {rank}");
            }
        }
    }

    [Check("scripting.bb-math-and-compare")]
    public static void MathAndComparisonFunctionsMatchTheShippedLua()
    {
        double M(BbMathOp op, double a, double b = 0) => (double)BbBaseLibrary.MathOperation(op, a, b)!;
        TestAssert.Equal(12.0, M(BbMathOp.MO_MULTIPLY, 3, 4));
        TestAssert.Equal(7.0, M(BbMathOp.MO_ADD, 3, 4));
        TestAssert.Equal(-1.0, M(BbMathOp.MO_SUBTRACT, 3, 4));
        TestAssert.Equal(0.25, M(BbMathOp.MO_DIVIDE, 1, 4));
        TestAssert.True(double.IsPositiveInfinity(M(BbMathOp.MO_DIVIDE, 1, 0)), "Lua divides by zero to inf");
        TestAssert.Equal(3.0, M(BbMathOp.MO_MIN, 3, 4));
        TestAssert.Equal(4.0, M(BbMathOp.MO_MAX, 3, 4));
        TestAssert.Equal(1.0, M(BbMathOp.MO_MODULO, 7, 3));
        TestAssert.Equal(2.0, M(BbMathOp.MO_MODULO, -7, 3), "Lua 5.1 modulo takes the sign of the divisor");
        TestAssert.Equal(3.0, M(BbMathOp.MO_ROUND, 2.5));
        TestAssert.Equal(-2.0, M(BbMathOp.MO_ROUND, -2.5), "round is floor(x + 0.5)");
        TestAssert.Equal(3.0, M(BbMathOp.MO_ROUNDUP, 2.1));
        TestAssert.Equal(-3.0, M(BbMathOp.MO_ROUNDDOWN, -2.1));
        TestAssert.Near(0.5, M(BbMathOp.MO_SIN, 30), 1e-12, "sin takes degrees");
        TestAssert.Near(0.5, M(BbMathOp.MO_COSINE, 60), 1e-12);
        TestAssert.Near(1.0, M(BbMathOp.MO_TANGENT, 45), 1e-12);
        TestAssert.Throws<BbRuntimeException>(() => BbBaseLibrary.MathOperation(BbMathOp.MO_ADD, null, 1.0), "arithmetic on nil");
        // Batch 218: arithmetic converts numeric string operands as Lua 5.1 does (luaV_tonumber); other strings still raise.
        TestAssert.Equal(2.0, (double)BbBaseLibrary.MathOperation(BbMathOp.MO_ADD, "1", 1.0)!,
            "a numeric string is coerced to its number");
        TestAssert.Throws<BbRuntimeException>(() => BbBaseLibrary.MathOperation(BbMathOp.MO_ADD, "abc", 1.0),
            "a string that is not a number still raises");

        var host = new OracleHost(new OracleCore());
        bool C(BbComparison op, object? a, object? b) => BbBaseLibrary.Compare(host, op, a, b);
        TestAssert.True(C(BbComparison.CO_EQUAL, "Annie", "annie"), "two strings compare without case");
        TestAssert.False(C(BbComparison.CO_EQUAL, "Annie", 5.0));
        TestAssert.True(C(BbComparison.CO_EQUAL, null, null));
        TestAssert.True(C(BbComparison.CO_NOT_EQUAL, 1.0, 2.0));
        TestAssert.True(C(BbComparison.CO_LESS_THAN, 1.0, 2.0) && C(BbComparison.CO_GREATER_THAN, 2.0, 1.0));
        TestAssert.True(C(BbComparison.CO_LESS_THAN_OR_EQUAL, 2.0, 2.0) && C(BbComparison.CO_GREATER_THAN_OR_EQUAL, 2.0, 2.0));
        TestAssert.True(C(BbComparison.CO_LESS_THAN, "a", "b"), "strings order by content");
        TestAssert.Throws<BbRuntimeException>(() => C(BbComparison.CO_LESS_THAN, 1.0, "b"), "mixed types do not compare");
        TestAssert.Throws<BbRuntimeException>(() => C(BbComparison.CO_LESS_THAN, null, 1.0), "nil does not compare");
        TestAssert.True(C(BbComparison.CO_SAME_TEAM, OracleCore.Owner, OracleCore.Caster) && C(BbComparison.CO_DIFFERENT_TEAM, OracleCore.Owner, OracleCore.Target));

        // CO_DAMAGE_SOURCETYPE_IS reads the context twice when the first read misses, as the shipped function does.
        var core = new OracleCore();
        var counted = new OracleHost(core);
        bool hit = BbBaseLibrary.Compare(counted, BbComparison.CO_DAMAGE_SOURCETYPE_IS, 99.0, 99.0);
        TestAssert.False(hit, "neither operand is the source type");
        TestAssert.Equal(2, core.Log.Count(l => l.StartsWith("GetSourceType", StringComparison.Ordinal)), "two reads");
        var firstRead = new OracleCore();
        BbBaseLibrary.Compare(new OracleHost(firstRead), BbComparison.CO_DAMAGE_SOURCETYPE_IS, 0.0, 5.0);
        TestAssert.Equal(1, firstRead.Log.Count, "a hit on the second operand needs one read");
    }

    [Check("scripting.bb-execute-periodically")]
    public static void ExecutePeriodicallyTracksTheClockInAVariable()
    {
        // OracleCore's clock advances 0.5 s per read: GetTime is 10.0, 10.5, 11.0, ...
        var every = BbTest.Block("BBExecutePeriodically", ["TimeBetweenExecutions", 1, "TrackTimeVar", "last", "TrackTimeVarTable", "InstanceVars", "ExecuteImmediately", true], BbTest.Increment("runs"));
        var run = new BbTest.Run([every, every, every, every, every]);
        TestAssert.Equal(3.0, run.Var("runs"), "10.0 (immediate), 11.0 and 12.0");
        TestAssert.Equal(12.0, run.Scope.InstanceVars!["last"]);
        var lazy = BbTest.Block("BBExecutePeriodically", ["TimeBetweenExecutions", 1, "TrackTimeVar", "last"], BbTest.Increment("runs"));
        TestAssert.Equal(2.0, new BbTest.Run([lazy, lazy, lazy, lazy, lazy]).Var("runs"), "without ExecuteImmediately the first call only starts the clock");
        // TickTimeVar overrides the period; Reset forgets the start time.
        var variable = BbTest.Block("BBExecutePeriodically", ["TimeBetweenExecutions", 100, "TickTimeVar", "period", "TrackTimeVar", "last", "ExecuteImmediately", false], BbTest.Increment("runs"));
        TestAssert.Equal(2.0, new BbTest.Run([BbTest.SetVar("period", 0.5), variable, variable, variable]).Var("runs"));
        var reset = BbTest.Block("BBExecutePeriodicallyReset", ["TrackTimeVar", "last", "TrackTimeVarTable", "InstanceVars"]);
        var again = new BbTest.Run([every, reset, every]);
        TestAssert.Equal(2.0, again.Var("runs"), "after a reset the next call starts over and runs immediately");
    }

    [Check("scripting.bb-base-library-flow")]
    public static void SmallerBaseFunctionsDoWhatTheBaseFileDoes()
    {
        // BBMath reads variables through GetMathNumber (variable first, literal as fallback) and writes the named table.
        var run = new BbTest.Run([
            BbTest.SetVar("w", 6, "InstanceVars"),
            BbTest.Block("BBMath", ["Src1Var", "w", "Src1VarTable", "InstanceVars", "Src1Value", 0, "Src2Value", 7, "DestVar", "product", "DestVarTable", "NextBuffVars", "MathOp", BbTest.Ref("MO_MULTIPLY")]),
            BbTest.Block("BBMath", ["Src1Var", "absent", "Src1Value", 5, "Src2Value", 1, "DestVar", "fallback", "MathOp", BbTest.Ref("MO_SUBTRACT")]),
        ]);
        TestAssert.Equal(42.0, run.Scope.NextBuffVars!["product"]);
        TestAssert.Equal(4.0, run.Var("fallback"));
        TestAssert.Equal(5.0, BbBaseLibrary.GetNumber(run.Scope, 5.0));
        TestAssert.Equal(4.0, BbBaseLibrary.GetNumber(run.Scope, "fallback"));
        TestAssert.True(BbBaseLibrary.GetNumber(run.Scope, true) is null, "anything else is nil");

        // BBWhile re-reads the variable each time round.
        var loop = new BbTest.Run([BbTest.SetVar("n", 0), BbTest.Block("BBWhile", ["Src1Var", "n", "Value2", 4, "CompareOp", BbTest.Ref("CO_LESS_THAN")], BbTest.Increment("n"), BbTest.Increment("sum"))]);
        TestAssert.Equal(4.0, loop.Var("n"));
        TestAssert.Equal(4.0, loop.Var("sum"));

        // The buff, level and distance getters store their host answer in the named table (created on demand when the base file says so).
        var info = new BbTest.Run([
            BbTest.Block("BBGetTeamID", ["TargetVar", "Attacker", "DestVar", "team"]),
            BbTest.Block("BBGetLevel", ["TargetVar", "Target", "DestVar", "level", "DestVarTable", "InstanceVars"]),
            BbTest.Block("BBGetTime", ["DestVar", "now", "DestVarTable", "CreatedByGetTime"]),
            BbTest.Block("BBGetBuffCountFromAll", ["TargetVar", "Owner", "BuffName", "Pyromania", "DestVar", "stacks"]),
        ]);
        TestAssert.Equal(200.0, info.Var("team"));
        TestAssert.Equal(4.0, info.Scope.InstanceVars!["level"], "BBGetLevel without a created table still finds the existing one");
        TestAssert.Equal(10.0, ((BbTable)info.Var("CreatedByGetTime")!)["now"]);
        TestAssert.Equal("SpellBuffCount(Owner, \"Pyromania\")", info.Core.Log.Single(l => l.StartsWith("SpellBuffCount", StringComparison.Ordinal)), "'from all' passes no caster");
        // BBGetLevel with no table name falls back to the root (GetTable without create), and BBGetUnitSkinName needs a target.
        var skin = new BbTest.Run([BbTest.Block("BBGetLevel", ["TargetVar", "Owner", "DestVar", "lvl", "DestVarTable", "NoSuchTable"]), BbTest.Block("BBGetUnitSkinName", ["DestVar", "skin"])]);
        TestAssert.Equal(5.0, skin.Var("lvl"));
        TestAssert.True(skin.Var("skin") is null && !skin.Core.Log.Any(l => l.StartsWith("GetUnitSkinName", StringComparison.Ordinal)), "no target, no call");

        // BBSpellBuffRemoveStacks removes one stack at a time; NumStacks = 0 means every stack the unit has.
        var remove = new BbTest.Run([BbTest.Block("BBSpellBuffRemoveStacks", ["TargetVar", "Owner", "AttackerVar", "Attacker", "BuffName", "Pyromania", "NumStacks", 2])]);
        TestAssert.Equal(2, remove.Core.Log.Count(l => l.StartsWith("SpellBuffRemove", StringComparison.Ordinal)));
        var all = new BbTest.Run([BbTest.Block("BBSpellBuffRemoveStacks", ["TargetVar", "Owner", "AttackerVar", "Attacker", "BuffName", "Pyromania", "NumStacks", 0])]);
        TestAssert.True(all.Core.Log[0].StartsWith("SpellBuffCount", StringComparison.Ordinal), "NumStacks 0 counts first");

        // BBBreakSpellShields adds the shield marker; the NextBuffVars table travels along.
        var shield = new BbTest.Run([BbTest.Block("BBBreakSpellShields", ["TargetVar", "Target"])]);
        TestAssert.Equal("SpellBuffAdd(Target, Target, \"SpellShieldMarker\", 0, 1, 37037, {})", shield.Core.Log.Single());

        // A Lua closure parameter cannot run without a Lua VM.
        TestAssert.Throws<BbRuntimeException>(() => new BbTest.Run([BbTest.Block("BBConditionallyExecute", ["IsConditionTrue", new BbValue.Closure()])]));
    }

    private static T Catch<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T ex) { return ex; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void CollectReferences(BbValue value, ISet<string> names)
    {
        switch (value)
        {
            case BbValue.Reference reference: names.Add(reference.Name); break;
            case BbValue.Table table:
                foreach (var item in table.Items) CollectReferences(item, names);
                foreach (var field in table.Fields) CollectReferences(field.Value, names);
                break;
        }
    }

    /// <summary>The same file set the M0 corpus check counts: Spells, Characters/*/Scripts, Items, Talents.</summary>
    internal static IReadOnlyList<string> ScriptFiles()
    {
        var files = new List<string>();
        foreach (var directory in new[] { "Spells", "Items", "Talents" })
            files.AddRange(Directory.EnumerateFiles(Paths.Data(directory), "*.luaobj").Select(f => $"{directory}/{Path.GetFileName(f)}"));
        foreach (var character in Directory.EnumerateDirectories(Paths.Data("Characters")))
        {
            var scripts = Path.Combine(character, "Scripts");
            if (Directory.Exists(scripts))
                files.AddRange(Directory.EnumerateFiles(scripts, "*.luaobj").Select(f => $"Characters/{Path.GetFileName(character)}/Scripts/{Path.GetFileName(f)}"));
        }
        return files.OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    private sealed class RecordingHandler : IBbNativeHandler
    {
        public List<string> Calls { get; } = new();
        public bool RunSubBlocks { get; init; }

        public void Execute(BbCall call)
        {
            Calls.Add(call.Function);
            if (RunSubBlocks) call.ExecuteSubBlocks();
        }
    }

    /// <summary>A host with the stub world for primitives and one recording handler per owner.</summary>
    private sealed class RoutingHost : IBbHost
    {
        private readonly OracleHost _inner = new(new OracleCore());
        private readonly IReadOnlyDictionary<BbNativeOwner, RecordingHandler> _handlers;

        public RoutingHost(IReadOnlyDictionary<BbNativeOwner, RecordingHandler> handlers) => _handlers = handlers;

        public IBbNativeHandler? NativeHandler(BbNativeOwner owner) => _handlers.GetValueOrDefault(owner);

        public double GetTeamID(object? unit) => _inner.GetTeamID(unit);
        public bool IsObjectAI(object? unit) => _inner.IsObjectAI(unit);
        public bool IsObjectHero(object? unit) => _inner.IsObjectHero(unit);
        public bool IsMelee(object? unit) => _inner.IsMelee(unit);
        public bool IsTurretAI(object? unit) => _inner.IsTurretAI(unit);
        public bool IsDead(object? unit) => _inner.IsDead(unit);
        public string? GetUnitSkinName(object? unit) => _inner.GetUnitSkinName(unit);
        public void SetScaleSkinCoef(object? scale, object? unit) => _inner.SetScaleSkinCoef(scale, unit);
        public object? GetPosition(object? unit) => _inner.GetPosition(unit);
        public object? GetNormalizedPositionDelta(object? target, object? attacker, bool flag) => _inner.GetNormalizedPositionDelta(target, attacker, flag);
        public void SetPosition(object? unit, object? position) => _inner.SetPosition(unit, position);
        public object? Make3DPoint(object? x, object? y, object? z) => _inner.Make3DPoint(x, y, z);
        public double DistanceBetweenObjects(object? a, object? b) => _inner.DistanceBetweenObjects(a, b);
        public double DistanceBetweenObjectAndPoint(object? unit, object? point) => _inner.DistanceBetweenObjectAndPoint(unit, point);
        public double DistanceBetweenPoints(object? a, object? b) => _inner.DistanceBetweenPoints(a, b);
        public bool IsTargetInFrontOfMe(object? me, object? target) => _inner.IsTargetInFrontOfMe(me, target);
        public bool IsTargetBehindMe(object? me, object? target) => _inner.IsTargetBehindMe(me, target);
        public bool HasBuffOfType(object? unit, double? buffType) => _inner.HasBuffOfType(unit, buffType);
        public double SpellBuffCount(object? unit, string? buffName, object? caster) => _inner.SpellBuffCount(unit, buffName, caster);
        public void SpellBuffAdd(object? target, object? attacker, string buffName, double addType, double maxStack, double duration, BbTable? vars) =>
            _inner.SpellBuffAdd(target, attacker, buffName, addType, maxStack, duration, vars);
        public void SpellBuffRemove(object? target, string? buffName, object? attacker) => _inner.SpellBuffRemove(target, buffName, attacker);
        public double GetSourceType() => _inner.GetSourceType();
        public double GetLevel(object? unit) => _inner.GetLevel(unit);
        public double GetArmor(object? unit) => _inner.GetArmor(unit);
        public double GetSpellBlock(object? unit) => _inner.GetSpellBlock(unit);
        public double GetTotalAttackDamage(object? unit) => _inner.GetTotalAttackDamage(unit);
        public void IncStat(BbIncStat stat, double delta, object? unit) => _inner.IncStat(stat, delta, unit);
        public double GetStat(BbGetStat stat, object? unit) => _inner.GetStat(stat, unit);
        public bool GetStatus(BbGetStatus status, object? unit) => _inner.GetStatus(status, unit);
        public void SetStatus(BbSetStatus status, object? unit, object? value) => _inner.SetStatus(status, unit, value);
        public bool HasPARType(object? unit, double? parType) => _inner.HasPARType(unit, parType);
        public object? GetResource(BbGetResource function, object? unit, double? parType) => _inner.GetResource(function, unit, parType);
        public double GetGold(BbCall call) => _inner.GetGold(call);
        public void ReincarnateNonDeadHero(object? unit) => _inner.ReincarnateNonDeadHero(unit);
        public object? GetSlotSpellInfo(BbSlotSpellInfo function, object? owner, object? slot, double? spellbook, double? slotType) =>
            _inner.GetSlotSpellInfo(function, owner, slot, spellbook, slotType);
        public void SetSlotSpellCooldownTime(object? owner, object? slot, double? spellbook, double? slotType, object? cooldown) =>
            _inner.SetSlotSpellCooldownTime(owner, slot, spellbook, slotType, cooldown);
        public void SetSpellCastRange(double? range) => _inner.SetSpellCastRange(range);
        public void SetCanCastWhileDisabled(bool canCast) => _inner.SetCanCastWhileDisabled(canCast);
        public object? GetCastInfo(BbCastInfo info, object? unit) => _inner.GetCastInfo(info, unit);
        public double GetTime() => _inner.GetTime();
        public double Random() => _inner.Random();
        public void Say(object? unit, string? text) => _inner.Say(unit, text);
        public void Alert(string? text) => _inner.Alert(text);
        public void IssueOrder(object? whom, double? order, object? position, object? target) => _inner.IssueOrder(whom, order, position, target);
        public void DefUpdateAura(object? center, double? range, double? unitScan, string? buffName) => _inner.DefUpdateAura(center, range, unitScan, buffName);
    }
}
