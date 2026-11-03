namespace Amberline.Scripting.BuildingBlocks.Runtime;

/// <summary>One block function: <c>function(scope, params, subBlocks)</c> in the Lua, a <see cref="BbCall"/> here.</summary>
public delegate void BbBlockFunction(BbCall call);

/// <summary>
/// A port of the functions <c>DATA/BuildingBlocks/BuildingBlocksBase.luaobj</c> defines (92 prototypes): the 52 block
/// functions, the helpers <c>GetTable</c>, <c>GetMathNumber</c> and <c>GetNumber</c>, the 23 <c>CO_*</c> comparisons and the
/// 13 <c>MO_*</c> operations. <c>ExecuteBuildingBlocks</c> is <see cref="BbInterpreter.Execute"/>. Each body was read from
/// the bytecode listing (the repo's lua listing tool) and keeps its evaluation order, because the order of
/// host calls is observable; the differential check scripting.bb-base-oracle replays shipped blocks through the repo's Lua
/// VM and compares every host call.
/// What the Lua does that this port does not: it raises its errors lazily where this raises at the lookup
/// (GetTable on a non-table), it coerces numeric strings in arithmetic, and BBTeleportToPoint also leaves three stray
/// globals (Xloc, Yloc, Zloc) behind. None of that is observable by the shipped scripts.
/// </summary>
public static class BbBaseLibrary
{
    private static readonly Dictionary<string, BbBlockFunction> Functions = new(StringComparer.Ordinal)
    {
        ["BBRequireVar"] = _ => { }, // an empty function in the base file
        ["BBConditionallyExecute"] = ConditionallyExecute,
        ["BBSetVarInTable"] = SetVarInTable,
        ["BBGetSlotSpellInfo"] = GetSlotSpellInfo,
        ["BBSetSlotSpellCooldownTime"] = SetSlotSpellCooldownTime,
        ["BBSetReturnValue"] = SetReturnValue,
        ["BBIf"] = If,
        ["BBElse"] = Else,
        ["BBElseIf"] = ElseIf,
        ["BBIfHasBuffOfType"] = IfHasBuffOfType,
        ["BBIfHasBuff"] = IfHasBuff,
        ["BBBreakExecution"] = c => c.Scope.Vars[BbScope.BreakExecutionKey] = true,
        ["BBIfNotHasBuff"] = IfNotHasBuff,
        ["BBIfPARTypeEquals"] = c => Branch(c, c.Host.HasPARType(Slot(c, "OwnerVar", "Owner"), BbRuntime.NumberOrNull(c.Value("PARType")))),
        ["BBIfPARTypeNotEquals"] = c => Branch(c, !c.Host.HasPARType(Slot(c, "OwnerVar", "Owner"), BbRuntime.NumberOrNull(c.Value("PARType")))),
        ["BBWhile"] = While,
        ["BBMath"] = MathBlock,
        ["BBKnockback"] = Knockback,
        ["BBIncStat"] = IncStat,
        ["BBIncPermanentStat"] = IncStat, // identical bytecode
        ["BBGetStat"] = GetStat,
        ["BBGetLevel"] = c => GetUnitNumber(c, (h, u) => h.GetLevel(u)),
        ["BBGetArmor"] = c => GetUnitNumber(c, (h, u) => h.GetArmor(u)),
        ["BBGetSpellBlock"] = c => GetUnitNumber(c, (h, u) => h.GetSpellBlock(u)),
        ["BBGetTeamID"] = c => GetUnitNumber(c, (h, u) => h.GetTeamID(u)),
        ["BBGetUnitSkinName"] = GetUnitSkinName,
        ["BBGetTotalAttackDamage"] = c => GetUnitNumber(c, (h, u) => h.GetTotalAttackDamage(u)),
        ["BBGetStatus"] = GetStatus,
        ["BBGetCastInfo"] = GetCastInfo,
        ["BBSetCanCastWhileDisabled"] = c => c.Host.SetCanCastWhileDisabled(BbRuntime.Truthy(c.Value("CanCast"))),
        ["BBExecutePeriodically"] = ExecutePeriodically,
        ["BBExecutePeriodicallyReset"] = c => c.Table("TrackTimeVarTable", false)[c.Value("TrackTimeVar")] = null,
        ["BBSetStatus"] = SetStatus,
        ["BBAlert"] = Alert,
        ["BBSay"] = Say,
        ["BBDebugSay"] = Say, // identical bytecode
        ["BBGetGold"] = c => Store(c, "DestVarTable", true, "DestVar", () => c.Host.GetGold(c)),
        ["BBTeleportToPoint"] = TeleportToPoint,
        ["BBDefUpdateAura"] = c => c.Host.DefUpdateAura(c.Table("CenterTable", false)[c.Value("CenterVar")],
            BbRuntime.NumberOrNull(c.Value("Range")), BbRuntime.NumberOrNull(c.Value("UnitScan")), c.Value("BuffName") as string),
        ["BBReincarnateHero"] = c => c.Host.ReincarnateNonDeadHero(c.Table("TargetTable", false)[c.Value("TargetVar")]),
        ["BBGetPAROrHealth"] = GetPAROrHealth,
        ["BBIssueOrder"] = IssueOrder,
        ["BBSetSpellCastRange"] = c => c.Host.SetSpellCastRange(BbRuntime.NumberOrNull(
            c.Value("NewRangeVar") is { } rangeName ? c.Table("NewRangeVarTable", false)[rangeName] : c.Value("NewRange"))),
        ["BBGetTime"] = c => Store(c, "DestVarTable", true, "DestVar", () => c.Host.GetTime()),
        ["BBDistanceBetweenObjects"] = DistanceBetweenObjects,
        ["BBDistanceBetweenObjectAndPoint"] = DistanceBetweenObjectAndPoint,
        ["BBDistanceBetweenPoints"] = DistanceBetweenPoints,
        ["BBGetBuffCountFromCaster"] = GetBuffCountFromCaster,
        ["BBGetBuffCountFromAll"] = GetBuffCountFromAll,
        ["BBSetScaleSkinCoef"] = SetScaleSkinCoef,
        ["BBBreakSpellShields"] = BreakSpellShields,
        ["BBSpellBuffRemoveStacks"] = SpellBuffRemoveStacks,
    };

    /// <summary>The block functions, by name.</summary>
    public static IReadOnlyCollection<string> BlockFunctionNames => Functions.Keys;

    public static bool TryGet(string name, out BbBlockFunction function) => Functions.TryGetValue(name, out function!);

    /// <summary>Every global the base file defines under that name (block functions, helpers, CO_* and MO_*).</summary>
    public static bool Contains(string name) =>
        Functions.ContainsKey(name) || BbNames<BbComparison>.Contains(name) || BbNames<BbMathOp>.Contains(name) ||
        name is "ExecuteBuildingBlocks" or "GetTable" or "GetMathNumber" or "GetNumber";

    // ------------------------------------------------------------------------------------------------------------
    // Helpers defined by the base file
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>GetTable(scope, name, create)</c>: the variable table called <paramref name="name"/>; with no name, or when it
    /// does not exist and <paramref name="create"/> is false, the root scope itself. With <paramref name="create"/> a missing
    /// table is created and stored first.
    /// </summary>
    public static BbTable GetTable(BbScope scope, object? name, bool create)
    {
        if (name is not null)
        {
            var existing = scope.Vars[name];
            if (existing is null && create)
            {
                var made = new BbTable();
                scope.Vars[name] = made;
                return made;
            }
            // Retail hands back the variable whatever it holds (proto 0.3), so a non-table fails only where the caller indexes it, which
            // can come after other checks or never (BBSetVarInTable reads SrcValue when no SrcVar is given): the stand-in table raises
            // Lua's "attempt to index" on its first use (BbTable.NotATable, batch 241).
            if (existing is not null) return existing as BbTable ?? BbTable.NotATable(existing);
        }
        return scope.Vars;
    }

    /// <summary><c>GetMathNumber(scope, table, var, value)</c>: the named variable when it is set, else the literal.</summary>
    public static object? GetMathNumber(BbScope scope, object? table, object? variable, object? value)
    {
        if (variable is not null && GetTable(scope, table, false)[variable] is { } found) return found;
        return value;
    }

    /// <summary><c>GetNumber(scope, x)</c>: a number is itself, a string names a root variable, a function is called with the scope.</summary>
    public static object? GetNumber(BbScope scope, object? x) => x switch
    {
        double => x,
        string name => scope.Vars[name],
        // Reconstruction: retail's GetNumber tail-calls x(scope) for a function value (BuildingBlocksBase proto 0.56). Engine
        // function values are host references, not closures, and no shipped script was found to pass one (not scanned exhaustively).
        BbFunctionRef f => throw new BbRuntimeException($"GetNumber cannot call the host function {f.Name}"),
        _ => null,
    };

    // ------------------------------------------------------------------------------------------------------------
    // CO_* and MO_*
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>The <c>CO_*</c> functions; each is called as <c>CompareOp(value1, value2)</c>.</summary>
    public static bool Compare(IBbHost host, BbComparison op, object? a, object? b)
    {
        switch (op)
        {
            case BbComparison.CO_EQUAL: return CaselessEquals(a, b);
            case BbComparison.CO_NOT_EQUAL: return !CaselessEquals(a, b);
            case BbComparison.CO_LESS_THAN: return BbRuntime.LessThan(a, b);
            case BbComparison.CO_GREATER_THAN: return BbRuntime.LessThan(b, a);
            case BbComparison.CO_LESS_THAN_OR_EQUAL: return BbRuntime.LessEqual(a, b);
            case BbComparison.CO_GREATER_THAN_OR_EQUAL: return BbRuntime.LessEqual(b, a);
            case BbComparison.CO_SAME_TEAM: return host.GetTeamID(a) == host.GetTeamID(b);
            case BbComparison.CO_DIFFERENT_TEAM: return host.GetTeamID(a) != host.GetTeamID(b);
            case BbComparison.CO_DAMAGE_SOURCETYPE_IS:
                // GetSourceType() == b or GetSourceType() == a; the second call only happens when the first fails.
                return BbRuntime.RawEquals(host.GetSourceType(), b) || BbRuntime.RawEquals(host.GetSourceType(), a);
            case BbComparison.CO_DAMAGE_SOURCETYPE_IS_NOT:
                return !(BbRuntime.RawEquals(host.GetSourceType(), b) || BbRuntime.RawEquals(host.GetSourceType(), a));
            case BbComparison.CO_IS_TYPE_AI: return host.IsObjectAI(a);
            case BbComparison.CO_IS_NOT_AI: return !host.IsObjectAI(a);
            case BbComparison.CO_IS_TYPE_HERO: return host.IsObjectHero(a);
            case BbComparison.CO_IS_NOT_HERO: return !host.IsObjectHero(a);
            case BbComparison.CO_IS_MELEE: return host.IsMelee(a);
            case BbComparison.CO_IS_RANGED: return !host.IsMelee(a);
            case BbComparison.CO_RANDOM_CHANCE_LESS_THAN: return BbRuntime.LessThan(host.Random(), a);
            case BbComparison.CO_IS_TYPE_TURRET: return host.IsTurretAI(a);
            case BbComparison.CO_IS_NOT_TURRET: return !host.IsTurretAI(a);
            case BbComparison.CO_IS_DEAD: return host.IsDead(a);
            case BbComparison.CO_IS_NOT_DEAD: return !host.IsDead(a);
            case BbComparison.CO_IS_TARGET_IN_FRONT_OF_ME: return host.IsTargetInFrontOfMe(a, b);
            case BbComparison.CO_IS_TARGET_BEHIND_ME: return host.IsTargetBehindMe(a, b);
            default: throw new BbRuntimeException($"unknown comparison {op}");
        }
    }

    // CO_EQUAL lower-cases both operands when both are strings (string.lower), otherwise it is Lua ==.
    private static bool CaselessEquals(object? a, object? b) =>
        a is string s && b is string t ? BbRuntime.AsciiEqualsIgnoreCase(s, t) : BbRuntime.RawEquals(a, b);

    /// <summary>The <c>MO_*</c> functions; each is called as <c>MathOp(value1, value2)</c> (the unary ones ignore the second).</summary>
    public static object? MathOperation(BbMathOp op, object? a, object? b)
    {
        switch (op)
        {
            case BbMathOp.MO_MULTIPLY: return BbRuntime.Number(a) * BbRuntime.Number(b);
            case BbMathOp.MO_ADD: return BbRuntime.Number(a) + BbRuntime.Number(b);
            case BbMathOp.MO_SUBTRACT: return BbRuntime.Number(a) - BbRuntime.Number(b);
            case BbMathOp.MO_DIVIDE: return BbRuntime.Number(a) / BbRuntime.Number(b);
            case BbMathOp.MO_MIN: return BbRuntime.LessThan(a, b) ? a : b;
            case BbMathOp.MO_MAX: return BbRuntime.LessThan(b, a) ? a : b;
            case BbMathOp.MO_MODULO:
            {
                var x = BbRuntime.Number(a);
                var y = BbRuntime.Number(b);
                return x - Math.Floor(x / y) * y; // Lua 5.1 luai_nummod
            }
            case BbMathOp.MO_ROUND: return Math.Floor(BbRuntime.Number(a) + 0.5);
            case BbMathOp.MO_ROUNDUP: return Math.Ceiling(BbRuntime.Number(a));
            case BbMathOp.MO_ROUNDDOWN: return Math.Floor(BbRuntime.Number(a));
            case BbMathOp.MO_SIN: return Math.Sin(BbRuntime.Number(a) * (Math.PI / 180.0)); // sin(math.rad(x))
            case BbMathOp.MO_COSINE: return Math.Cos(BbRuntime.Number(a) * (Math.PI / 180.0));
            case BbMathOp.MO_TANGENT: return Math.Tan(BbRuntime.Number(a) * (Math.PI / 180.0));
            default: throw new BbRuntimeException($"unknown math operation {op}");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Shared patterns
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Runs the sub-blocks when <paramref name="condition"/> holds and records the outcome for a following BBElse.</summary>
    private static void Branch(BbCall c, bool condition)
    {
        if (condition)
        {
            c.ExecuteSubBlocks();
            c.Scope.Vars[BbScope.LastIfSucceededKey] = true;
        }
        else c.Scope.Vars[BbScope.LastIfSucceededKey] = false;
    }

    /// <summary>The unit a block names: <c>scope[params.varKey]</c>, or the scope slot <paramref name="defaultSlot"/> when the param is absent.</summary>
    private static object? Slot(BbCall c, string varKey, string defaultSlot) =>
        c.Value(varKey) is not null ? c.Var(varKey) : c.Scope.Vars[defaultSlot];

    /// <summary><c>GetTable(scope, params.tableKey, create)[params.varKey] = value</c>.</summary>
    private static void Store(BbCall c, string tableKey, bool create, string varKey, Func<object?> value)
    {
        var table = c.Table(tableKey, create);
        var key = c.Value(varKey);
        table[key] = value();
    }

    /// <summary>The numeric <c>Src</c> operand of a block: the variable when the block names one, else the literal.</summary>
    private static object? Operand(BbCall c, BbTable table, string varKey, string valueKey) =>
        c.Value(varKey) is { } name ? table[name] : c.Value(valueKey);

    // ------------------------------------------------------------------------------------------------------------
    // Block functions
    // ------------------------------------------------------------------------------------------------------------

    private static void ConditionallyExecute(BbCall c)
    {
        // Retail (BuildingBlocksBase proto 0.2) calls params.IsConditionTrue(scope) unconditionally, so a missing key is a call
        // of nil, as below. A present key is a Lua closure whose body the extracted tables do not keep, so it cannot run here.
        // Reconstruction: no shipped file was found to name IsConditionTrue (a grep of the shipped data, not a full scan).
        if (c.Param("IsConditionTrue") is null) throw new BbRuntimeException("attempt to call a nil value (field 'IsConditionTrue')");
        throw new BbRuntimeException("BBConditionallyExecute: IsConditionTrue is a Lua closure and cannot be run without a Lua VM");
    }

    private static void SetVarInTable(BbCall c)
    {
        var dest = c.Table("DestVarTable", true);
        if (c.Scope.Vars[BbScope.LevelKey] is { } level && c.Param("SrcValueByLevel") is { } byLevel)
        {
            dest[c.Value("DestVar")] = BbRuntime.Index(byLevel, level);
            return;
        }
        var src = c.Table("SrcVarTable", false);
        dest[c.Value("DestVar")] = c.Value("SrcVar") is { } name ? src[name] : c.Value("SrcValue");
    }

    private static void SetReturnValue(BbCall c)
    {
        var scope = c.Scope;
        if (scope.Vars[BbScope.LevelKey] is { } level && c.Param("SrcValueByLevel") is { } byLevel)
        {
            scope.Vars[BbScope.ReturnValueKey] = BbRuntime.Index(byLevel, level);
            return;
        }
        var src = c.Table("SrcVarTable", false);
        scope.Vars[BbScope.ReturnValueKey] = c.Value("SrcVar") is { } name ? src[name] : c.Value("SrcValue");
    }

    private static void GetSlotSpellInfo(BbCall c)
    {
        var dest = c.Table("DestVarTable", true);
        var slots = c.Table("SpellSlotVarTable", false);
        var slot = c.Value("SpellSlotVar") is { } name ? slots[name] : c.Value("SpellSlotValue");
        var info = c.Host.GetSlotSpellInfo(c.Accessor<BbSlotSpellInfo>("Function"), c.Var("OwnerVar"), slot,
            BbRuntime.NumberOrNull(c.Value("SpellbookType")), BbRuntime.NumberOrNull(c.Value("SlotType")));
        dest[c.Value("DestVar")] = info;
    }

    private static void SetSlotSpellCooldownTime(BbCall c)
    {
        var src = c.Table("SrcVarTable", false);
        var cooldown = c.Value("SrcVar") is { } srcName ? src[srcName] : c.Value("SrcValue");
        var slots = c.Table("SpellSlotVarTable", false);
        var slot = c.Value("SpellSlotVar") is { } slotName ? slots[slotName] : c.Value("SpellSlotValue");
        c.Host.SetSlotSpellCooldownTime(c.Var("OwnerVar"), slot, BbRuntime.NumberOrNull(c.Value("SpellbookType")),
            BbRuntime.NumberOrNull(c.Value("SlotType")), cooldown);
    }

    private static BbComparison CompareOp(BbCall c) =>
        c.Param("CompareOp") is BbValue.Reference reference && BbNames<BbComparison>.TryParse(reference.Name, out var op)
            ? op
            : throw new BbRuntimeException($"{c.Function}: CompareOp is not a CO_* function");

    private static bool Condition(BbCall c, BbTable src1, BbTable src2)
    {
        var value1 = Operand(c, src1, "Src1Var", "Value1");
        var op = CompareOp(c);
        var value2 = Operand(c, src2, "Src2Var", "Value2");
        return Compare(c.Host, op, value1, value2);
    }

    private static void If(BbCall c)
    {
        var src1 = c.Table("Src1VarTable", false);
        var src2 = c.Table("Src2VarTable", false);
        Branch(c, Condition(c, src1, src2));
    }

    private static void Else(BbCall c)
    {
        if (c.Scope.Vars[BbScope.LastIfSucceededKey] is not false) return;
        c.ExecuteSubBlocks();
        c.Scope.Vars[BbScope.LastIfSucceededKey] = true;
    }

    private static void ElseIf(BbCall c)
    {
        if (c.Scope.Vars[BbScope.LastIfSucceededKey] is not false) return;
        If(c);
    }

    private static void While(BbCall c)
    {
        var src1 = c.Table("Src1VarTable", false);
        var src2 = c.Table("Src2VarTable", false);
        while (Condition(c, src1, src2)) c.ExecuteSubBlocks();
    }

    private static void IfHasBuffOfType(BbCall c)
    {
        var target = Slot(c, "TargetVar", "Target");
        Branch(c, c.Host.HasBuffOfType(target, BbRuntime.NumberOrNull(c.Value("BuffType"))));
    }

    private static void IfHasBuff(BbCall c)
    {
        var owner = Slot(c, "OwnerVar", "Owner");
        var attacker = Slot(c, "AttackerVar", "Attacker");
        Branch(c, 0.0 < c.Host.SpellBuffCount(owner, c.Value("BuffName") as string, attacker));
    }

    private static void IfNotHasBuff(BbCall c)
    {
        var owner = Slot(c, "OwnerVar", "Owner");
        var caster = Slot(c, "CasterVar", "Caster");
        Branch(c, c.Host.SpellBuffCount(owner, c.Value("BuffName") as string, caster) <= 0.0);
    }

    private static BbMathOp MathOp(BbCall c) =>
        c.Param("MathOp") is BbValue.Reference reference && BbNames<BbMathOp>.TryParse(reference.Name, out var op)
            ? op
            : throw new BbRuntimeException($"{c.Function}: MathOp is not an MO_* function");

    private static void MathBlock(BbCall c)
    {
        var a = GetMathNumber(c.Scope, c.Value("Src1VarTable"), c.Value("Src1Var"), c.Value("Src1Value"));
        var b = GetMathNumber(c.Scope, c.Value("Src2VarTable"), c.Value("Src2Var"), c.Value("Src2Value"));
        var dest = c.Table("DestVarTable", false);
        var key = c.Value("DestVar");
        dest[key] = MathOperation(MathOp(c), a, b);
    }

    private static BbTable AsTable(object? value) =>
        value as BbTable ?? throw new BbRuntimeException($"attempt to index a {BbRuntime.TypeName(value)} value");

    private static void Knockback(BbCall c)
    {
        var host = c.Host;
        var state = AsTable(c.Scope.Vars["InstanceVars"]);
        if (state["InterpDelta"] is null)
        {
            var amount = c.Value("Amount");
            if (c.Value("AmountVar") is { } amountVar)
                amount = c.Table("AmountVarTable", false)[amountVar];
            state["KnockBackStart"] = host.GetPosition(c.Var("TargetVar"));
            var delta = AsTable(host.GetNormalizedPositionDelta(c.Var("TargetVar"), c.Var("AttackerVar"), true));
            var step = BbTable.Point(BbRuntime.Number(delta["x"]) * BbRuntime.Number(amount), 0.0, BbRuntime.Number(delta["z"]) * BbRuntime.Number(amount));
            state["InterpDelta"] = step;
            state["StartTime"] = host.GetTime();
            state["KnockBackDuration"] = c.Value("KnockBackDuration");
        }
        var target = c.Var("TargetVar");
        var progress = (BbRuntime.Number(host.GetTime()) - BbRuntime.Number(state["StartTime"])) / BbRuntime.Number(state["KnockBackDuration"]);
        var start = AsTable(state["KnockBackStart"]);
        var interp = AsTable(state["InterpDelta"]);
        var position = new BbTable();
        position["x"] = BbRuntime.Number(start["x"]) + BbRuntime.Number(interp["x"]) * progress;
        position["y"] = start["y"];
        position["z"] = BbRuntime.Number(start["z"]) + BbRuntime.Number(interp["z"]) * progress;
        host.SetPosition(target, position);
    }

    private static void IncStat(BbCall c)
    {
        var delta = 0.0;
        if (c.Has("Delta")) delta += BbRuntime.Number(c.Value("Delta"));
        if (c.Param("DeltaByLevel") is { } byLevel && c.Scope.Vars[BbScope.LevelKey] is { } level)
            delta += BbRuntime.Number(BbRuntime.Index(byLevel, level));
        if (c.Value("DeltaVar") is { } deltaVar)
            delta += BbRuntime.Number(c.Table("DeltaVarTable", true)[deltaVar]);
        var stat = c.Accessor<BbIncStat>("Stat");
        c.Host.IncStat(stat, delta, c.Has("TargetVar") ? c.Var("TargetVar") : null);
    }

    private static void GetStat(BbCall c)
    {
        var dest = c.Table("DestVarTable", false);
        var stat = c.Accessor<BbGetStat>("Stat");
        var value = c.Host.GetStat(stat, c.Has("TargetVar") ? c.Var("TargetVar") : null);
        dest[c.Value("DestVar")] = value;
    }

    /// <summary>BBGetLevel, BBGetArmor, BBGetSpellBlock, BBGetTeamID, BBGetTotalAttackDamage: the host getter with the target, or without it.</summary>
    private static void GetUnitNumber(BbCall c, Func<IBbHost, object?, double> get)
    {
        var dest = c.Table("DestVarTable", false);
        var value = get(c.Host, c.Has("TargetVar") ? c.Var("TargetVar") : null);
        dest[c.Value("DestVar")] = value;
    }

    private static void GetUnitSkinName(BbCall c)
    {
        var dest = c.Table("DestVarTable", false);
        if (!c.Has("TargetVar")) return;
        dest[c.Value("DestVar")] = c.Host.GetUnitSkinName(c.Var("TargetVar"));
    }

    private static void GetStatus(BbCall c)
    {
        var dest = c.Table("DestVarTable", true);
        var value = c.Host.GetStatus(c.Accessor<BbGetStatus>("Status"), c.Var("TargetVar"));
        dest[c.Value("DestVar")] = value;
    }

    private static void GetCastInfo(BbCall c)
    {
        var dest = c.Table("DestVarTable", true);
        var value = c.Host.GetCastInfo(c.Accessor<BbCastInfo>("Info"), c.Var("TargetVar"));
        dest[c.Value("DestVar")] = value;
    }

    private static void SetStatus(BbCall c)
    {
        var value = c.Value("SrcValue");
        if (c.Value("SrcVar") is { } name) value = c.Table("SrcTable", true)[name];
        c.Host.SetStatus(c.Accessor<BbSetStatus>("Status"), c.Var("TargetVar"), value);
    }

    private static void ExecutePeriodically(BbCall c)
    {
        var track = c.Table("TrackTimeVarTable", false);
        var now = c.Host.GetTime();
        var immediately = c.Value("ExecuteImmediately");
        var period = c.Value("TimeBetweenExecutions");
        if (c.Value("TickTimeVar") is { } tickVar && c.Table("TickTimeVarTable", false)[tickVar] is { } tick) period = tick;
        var key = c.Value("TrackTimeVar");
        if (track[key] is null)
        {
            track[key] = now;
            if (BbRuntime.RawEquals(immediately, true)) c.ExecutePeriodicSubBlocks();
        }
        // No else: the due check below runs right after an initialising call too.
        // Batch 142 part 2: MatchRuntime.Tick(1.0) splits the second into sub-steps of 1/30, so the clock reaches
        // this call as repeated additions of 1/30 and can sit a few ULP under the exact track + period line. A
        // strict <= then pushes a due block into the next second (SummonerDot's fifth pulse landed one frame late
        // and never fired). One nanosecond is the same slack the buff ticker keeps for the same kind of clock.
        // A due block runs for a due time inside the buff's own life: one that lands on the instant the buff ends
        // does not run, the buff being gone before another update could catch it (see IBbBuffHost.CurrentBuffEndsAt).
        var dueAt = BbRuntime.Number(track[key]) + BbRuntime.Number(period);
        var endsAt = c.Host.CurrentBuffEndsAt;
        if (BbRuntime.LessEqual(dueAt, now + 1e-9) && (endsAt is not { } end || dueAt <= end - 1e-9))
        {
            track[key] = dueAt;
            // The body is bracketed as a periodic's own (ExecutePeriodicSubBlocks): its permanent stat and vision
            // mutations accumulate on this periodic's clock, not on the buff's mutation clock (TurretDecay's and
            // ResistantSkin's 60 s pair).
            c.ExecutePeriodicSubBlocks();
        }
    }

    private static void Alert(BbCall c)
    {
        var text = c.Value("ToAlert");
        var table = c.Table("SrcVarTable", false);
        if (c.Value("SrcVar") is { } name) text = BbRuntime.Concat(text, table[name]);
        c.Host.Alert(text as string ?? (text is null ? null : BbRuntime.ToStringValue(text)));
    }

    private static void Say(BbCall c)
    {
        var text = c.Value("ToSay");
        var table = c.Table("SrcVarTable", false);
        if (c.Value("SrcVar") is { } name) text = BbRuntime.Concat(text, BbRuntime.ToStringValue(table[name]));
        var owner = Slot(c, "OwnerVar", "Owner");
        c.Host.Say(owner, text as string ?? (text is null ? null : BbRuntime.ToStringValue(text)));
    }

    private static void TeleportToPoint(BbCall c)
    {
        object? Coordinate(string tableKey, string varKey, string valueKey)
        {
            var table = c.Table(tableKey, true);
            return c.Value(varKey) is { } name ? table[name] : c.Value(valueKey);
        }

        var x = Coordinate("XVarTable", "XVar", "X");
        var y = Coordinate("YVarTable", "YVar", "Y");
        var z = Coordinate("ZVarTable", "ZVar", "Z");
        c.Scope.Vars["position"] = c.Host.Make3DPoint(x, y, z);
        // The Lua (proto 0.77) writes params.CastPositionName = "position" into the block's own params table and leaves the globals
        // Xloc, Yloc and Zloc behind; the native gets a copy with the field set instead. Retail-equivalent (batch 241): the block's
        // params are read by this function and the BBTeleportToPosition it calls only, which always set the same field, the three
        // globals are read by nothing but this function (no other shipped .luaobj names them), and no shipped script calls
        // BBTeleportToPoint at all.
        var parameters = c.Params.ToList();
        var position = new BbField("CastPositionName", new BbValue.Text("position"));
        var existing = parameters.FindIndex(p => p.Key == "CastPositionName");
        if (existing >= 0) parameters[existing] = position;
        else parameters.Add(position);
        c.Interpreter.Dispatch(new BbBlock("BBTeleportToPosition", parameters, Array.Empty<BbBlock>()), c.Scope);
    }

    private static void GetPAROrHealth(BbCall c)
    {
        var dest = c.Table("DestVarTable", true);
        var value = c.Host.GetResource(c.Accessor<BbGetResource>("Function"), c.Var("OwnerVar"),
            BbRuntime.NumberOrNull(c.Value("PARType")));
        dest[c.Value("DestVar")] = value;
    }

    private static void IssueOrder(BbCall c)
    {
        var whom = c.Var("WhomToOrderVar");
        var target = c.Var("TargetOfOrderVar");
        c.Host.IssueOrder(whom, BbRuntime.NumberOrNull(c.Value("Order")), c.Host.GetPosition(target), target);
    }

    private static void DistanceBetweenObjects(BbCall c)
    {
        var a = c.Var("ObjectVar1");
        var b = c.Var("ObjectVar2");
        var dest = c.Table("DestVarTable", true);
        dest[c.Value("DestVar")] = c.Host.DistanceBetweenObjects(a, b);
    }

    private static void DistanceBetweenObjectAndPoint(BbCall c)
    {
        var unit = c.Var("ObjectVar");
        var point = c.Table("PointVarTable", true)[c.Value("PointVar")];
        var dest = c.Table("DestVarTable", true);
        dest[c.Value("DestVar")] = c.Host.DistanceBetweenObjectAndPoint(unit, point);
    }

    private static void DistanceBetweenPoints(BbCall c)
    {
        var a = c.Table("Point1VarTable", true)[c.Value("Point1Var")];
        var b = c.Table("Point2VarTable", true)[c.Value("Point2Var")];
        var dest = c.Table("DestVarTable", true);
        dest[c.Value("DestVar")] = c.Host.DistanceBetweenPoints(a, b);
    }

    private static void GetBuffCountFromCaster(BbCall c)
    {
        var target = c.Var("TargetVar");
        var caster = c.Var("CasterVar");
        var dest = c.Table("DestVarTable", true);
        dest[c.Value("DestVar")] = c.Host.SpellBuffCount(target, c.Value("BuffName") as string, caster);
    }

    private static void GetBuffCountFromAll(BbCall c)
    {
        var target = c.Var("TargetVar");
        var dest = c.Table("DestVarTable", true);
        // The Lua passes the global `caster`, which the stock client never defines: nil, so every caster counts.
        dest[c.Value("DestVar")] = c.Host.SpellBuffCount(target, c.Value("BuffName") as string, null);
    }

    private static void SetScaleSkinCoef(BbCall c)
    {
        var scales = c.Table("ScaleVarTable", false);
        var owner = Slot(c, "OwnerVar", "Owner");
        if (c.Value("ScaleVar") is { } name && c.Value("ScaleVarTable") is not null)
            c.Host.SetScaleSkinCoef(scales[name], owner);
        else
            c.Host.SetScaleSkinCoef(c.Value("Scale"), owner);
    }

    private static void BreakSpellShields(BbCall c)
    {
        var target = c.Var("TargetVar");
        c.Host.SpellBuffAdd(target, target, "SpellShieldMarker", 0.0, 1.0, 37037.0, c.Scope.Vars["NextBuffVars"] as BbTable);
    }

    private static void SpellBuffRemoveStacks(BbCall c)
    {
        var target = c.Var("TargetVar");
        var stacks = c.Value("NumStacks");
        var buff = c.Value("BuffName") as string;
        if (BbRuntime.RawEquals(c.Value("NumStacks"), 0.0))
            stacks = c.Host.SpellBuffCount(target, buff, null); // caster: the same undefined global as BBGetBuffCountFromAll
        // The Lua removes one buff per count; the host receives the count (see IBbBuffHost.SpellBuffRemoveStacks).
        if (BbRuntime.LessThan(0.0, stacks))
            c.Host.SpellBuffRemoveStacks(target, buff, c.Var("AttackerVar"), BbRuntime.Number(stacks));
    }
}
