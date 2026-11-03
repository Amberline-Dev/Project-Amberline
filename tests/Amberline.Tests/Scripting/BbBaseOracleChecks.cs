using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Tools;

namespace Amberline.Tests;

/// <summary>
/// Differential checks of the BuildingBlocks interpreter against the shipped Lua. The Lua side is the repo's offline VM
/// (Tools/LuaVm.cs) running DATA/BuildingBlocks/BuildingBlocksBase.luaobj itself, so the expected behaviour of
/// ExecuteBuildingBlocks, BBIf and the other 90 functions is read from the retail bytecode, not from the port. The VM is a
/// test oracle only; production has no Lua VM. See BbOracleHarness.cs for what is compared.
/// </summary>
public static class BbBaseOracleChecks
{
    private static readonly double[] Levels = [1, 3, 5];

    [Check("scripting.bb-base-oracle")]
    public static void AnnieAndSorakaClosuresMatchTheShippedLua()
    {
        var paths = GameDataPaths.Default;
        var resolver = new BbScriptResolver(paths);
        var failures = new List<string>();
        long executed = 0, blocks = 0, hostCalls = 0;
        foreach (var champion in new[] { "Annie", "Soraka" })
        {
            var closure = resolver.Resolve(champion);
            TestAssert.Equal(0, closure.Diagnostics.Count, champion + " closure diagnostics");
            foreach (var entry in closure.Scripts)
            {
                var plan = Plan(entry.Script);
                blocks += plan.Sum(p => Count(p.Blocks));
                foreach (var level in Levels)
                    foreach (var seeded in new[] { false, true })
                    {
                        var (lua, bb, run) = Both(paths, plan, level, seeded);
                        Compare($"{champion}/{entry.Name}@{level}{(seeded ? "+vars" : "")}", lua, bb, failures);
                        executed += run;
                        hostCalls += bb.Log.Count(l => !l.StartsWith("==", StringComparison.Ordinal));
                    }
            }
        }
        var reachedStat = executed / (double)(2 * Levels.Length * blocks);
        Console.WriteLine($"bb-base-oracle: {blocks} planned blocks per rank, {executed / (2 * Levels.Length)} run per rank ({reachedStat:P0}), {hostCalls} log lines compared");
        TestAssert.True(failures.Count == 0, $"{failures.Count} divergence(s) from BuildingBlocksBase.luaobj: " + string.Join(" | ", failures.Take(6)));
        // The replay must reach most of the code it claims to compare (three ranks, so executed/3 per rank).
        var reached = executed / (double)(2 * Levels.Length * blocks);
        TestAssert.True(reached >= 0.5, $"only {reached:P0} of the {blocks} Annie and Soraka blocks ran per rank");
        TestAssert.True(hostCalls > 300, $"only {hostCalls} host calls compared");
    }

    [Check("scripting.bb-base-oracle-synthetic")]
    public static void EveryBaseFunctionMatchesTheShippedLua()
    {
        var paths = GameDataPaths.Default;
        var failures = new List<string>();
        foreach (var (name, blocks) in Scenarios())
        {
            foreach (var level in new double[] { 2, 5 })
            {
                var (lua, bb, _) = Both(paths, [("hook", blocks)], level);
                Compare($"{name}@{level}", lua, bb, failures);
            }
        }
        TestAssert.True(failures.Count == 0, $"{failures.Count} divergence(s) from BuildingBlocksBase.luaobj: " + string.Join(" | ", failures.Take(6)));
    }

    [Check("scripting.bb-base-oracle-corpus")]
    public static void EveryShippedScriptMatchesTheShippedLua()
    {
        var paths = GameDataPaths.Default;
        var resolver = new BbScriptResolver(paths);
        var failures = new List<string>();
        var scripts = 0;
        long executed = 0, errors = 0, total = 0;
        var reasons = new Dictionary<string, int>();
        foreach (var relative in BbRuntimeChecks.ScriptFiles())
        {
            var script = resolver.Load(relative).Script;
            if (script is null || !script.Events.Any(e => e.Blocks.Count > 0)) continue;
            scripts++;
            var plan = Plan(script);
            total += plan.Sum(p => Count(p.Blocks));
            foreach (var (level, seeded) in new[] { (1.0, false), (4.0, false), (3.0, true) })
            {
                var (lua, bb, run) = Both(paths, plan, level, seeded);
                Compare($"{script.Name}@{level}{(seeded ? "+vars" : "")}", lua, bb, failures);
                executed += run;
                errors += bb.Errors;
                foreach (var text in bb.ErrorTexts)
                {
                    var key = System.Text.RegularExpressions.Regex.Replace(text, @" \[block [^\]]*\]", "");
                    reasons[key] = reasons.GetValueOrDefault(key) + 1;
                }
            }
        }
        TestAssert.True(scripts > 1900, $"expected the block-bearing scripts of the corpus, saw {scripts}");
        TestAssert.True(failures.Count == 0, $"{failures.Count} of {scripts} scripts diverge from BuildingBlocksBase.luaobj: " + string.Join(" | ", failures.Take(6)));
        Console.WriteLine($"bb-base-oracle-corpus: {scripts} scripts, {executed} blocks run of {3 * total} planned, {errors} hook runs ended in a Lua error on both sides: " +
            string.Join("; ", reasons.OrderByDescending(r => r.Value).Take(14).Select(r => $"{r.Value}x {r.Key}")));
    }

    [Check("tools.lua.vm.nil-key-and-string-order")]
    public static void LuaVmReadsNilKeysAndComparesStrings()
    {
        // Lua: t[nil] reads nil and writing it raises; < and <= order two strings (the oracle needs both).
        var table = new LuaTable();
        TestAssert.True(table.Get(null) is null, "t[nil] reads nil");
        TestAssert.Throws<LuaVmException>(() => table.Set(null, 1.0), "t[nil] = x raises");
        var lua = new OracleLua(GameDataPaths.Default);
        TestAssert.Equal(true, lua.CallGlobal("CO_LESS_THAN", "Annie", "annie")[0], "'A' < 'a'");
        TestAssert.Equal(false, lua.CallGlobal("CO_LESS_THAN", "b", "a")[0]);
        TestAssert.Equal(true, lua.CallGlobal("CO_GREATER_THAN", "b", "a")[0]);
        TestAssert.Equal(true, lua.CallGlobal("CO_LESS_THAN_OR_EQUAL", "a", "a")[0]);
        TestAssert.Equal(true, lua.CallGlobal("CO_LESS_THAN", 1.0, 2.0)[0], "numbers still compare");
        TestAssert.Throws<LuaVmException>(() => lua.CallGlobal("CO_LESS_THAN", 1.0, "a"), "mixed types still raise");
    }

    private static IReadOnlyList<(string Hook, IReadOnlyList<BbBlock> Blocks)> Plan(BbScript script) =>
        script.Events.Select(e => (e.Hook, e.Blocks)).ToList();

    private static int Count(IReadOnlyList<BbBlock> blocks) => blocks.Sum(b => 1 + Count(b.SubBlocks));

    private static (OracleOutcome Lua, OracleOutcome Bb, long Executed) Both(GameDataPaths paths,
        IReadOnlyList<(string Hook, IReadOnlyList<BbBlock> Blocks)> plan, double level, bool seeded = false)
    {
        var seeds = seeded ? OracleSeeds.For(plan) : null;
        var lua = new OracleLua(paths).Run(plan, level, seeds);
        var bb = OracleBb.Run(plan, level, out var executed, seeds);
        return (lua, bb, executed);
    }

    private static void Compare(string label, OracleOutcome lua, OracleOutcome bb, List<string> failures)
    {
        var length = Math.Min(lua.Log.Count, bb.Log.Count);
        for (var i = 0; i < length; i++)
        {
            if (lua.Log[i] == bb.Log[i] || ReadsNewRangeVar(lua.Log[i], bb.Log[i])) continue;
            failures.Add($"{label}: call #{i} Lua `{Trim(lua.Log[i])}` vs port `{Trim(bb.Log[i])}`");
            return;
        }
        if (lua.Log.Count != bb.Log.Count)
            failures.Add($"{label}: Lua made {lua.Log.Count} host calls, the port {bb.Log.Count}; next `{Trim(lua.Log.Count > length ? lua.Log[length] : bb.Log[length])}`");
        else if (lua.Scope != bb.Scope) failures.Add($"{label}: final scope Lua `{Trim(lua.Scope)}` vs port `{Trim(bb.Scope)}`");
        else if (lua.Globals != bb.Globals) failures.Add($"{label}: pseudo-globals Lua `{lua.Globals}` vs port `{bb.Globals}`");
        else if (lua.Errors != bb.Errors) failures.Add($"{label}: {lua.Errors} Lua errors vs {bb.Errors} in the port");
    }

    /// <summary>
    /// The one recorded deviation from the shipped base library (batch 114, W25c): <c>BBSetSpellCastRange</c> in the Lua passes <c>NewRange</c> and drops
    /// <c>NewRangeVar</c>, so Buster Shot's and Flash's AdjustCastInfo say 0 there; the native is server-only and the port reads the variable (the scripts keep
    /// their cast range in it: 960 and 750 here). Only this call's value may differ.
    /// </summary>
    private static bool ReadsNewRangeVar(string lua, string port) =>
        lua.StartsWith("SetSpellCastRange(", StringComparison.Ordinal) && port.StartsWith("SetSpellCastRange(", StringComparison.Ordinal);

    private static string Trim(string text) => text.Length <= 400 ? text : text[..400] + "...";

    // ---- hand-built blocks for functions the Annie and Soraka scripts never reach ----

    private static IEnumerable<(string Name, IReadOnlyList<BbBlock> Blocks)> Scenarios()
    {
        var ref_ = (Func<string, BbValue>)BbTest.Ref;
        var table = new BbValue.Table([new BbValue.Number(10), new BbValue.Number(20), new BbValue.Number(30), new BbValue.Number(40), new BbValue.Number(50)], []);

        // Every comparison on units, numbers, strings and mixed operands, one scenario each so an error in one
        // does not hide the rest (both engines must still fail at the same call).
        foreach (var op in Enum.GetNames<BbComparison>())
        {
            var operands = new (string Kind, string Left, string Right)[]
            {
                ("units", "Owner", "Target"), ("same-team", "Owner", "Caster"), ("numbers", "n1", "n2"),
                ("strings", "s1", "s2"), ("mixed", "n2", "s1"), ("nil", "missing", "n1"),
            };
            foreach (var (kind, left, right) in operands)
                yield return ($"compare-{op}-{kind}", [
                    BbTest.SetVar("n1", 3), BbTest.SetVar("n2", 7), BbTest.SetVar("s1", "Annie"), BbTest.SetVar("s2", "annie"),
                    BbTest.Block("BBIf", ["Src1Var", left, "Src2Var", right, "CompareOp", ref_(op)], BbTest.Increment("hit")),
                    BbTest.Block("BBElse", [], BbTest.Increment("miss")),
                    BbTest.Block("BBIf", ["Src1Var", left, "Value2", 5, "CompareOp", ref_(op)], BbTest.Increment("hitLiteral")),
                    BbTest.Block("BBElseIf", ["Value1", 4, "Src2Var", right, "CompareOp", ref_(op)], BbTest.Increment("hitElseIf")),
                ]);
        }

        foreach (var op in Enum.GetNames<BbMathOp>())
            foreach (var (a, b) in new[] { (7.0, 3.0), (-2.5, 4.0), (0.0, 0.0), (1e15, 0.3), (2.5, -0.5) })
                yield return ($"math-{op}-{a}-{b}", [BbTest.Block("BBMath", ["Src1Value", a, "Src2Value", b, "DestVar", "r", "MathOp", ref_(op)])]);
        yield return ("math-nil", [BbTest.Block("BBMath", ["Src1Var", "nothing", "Src2Value", 1, "DestVar", "r", "MathOp", ref_("MO_ADD")])]);
        yield return ("math-min-strings", [BbTest.SetVar("a", "b"), BbTest.SetVar("b", "a"),
            BbTest.Block("BBMath", ["Src1Var", "a", "Src2Var", "b", "DestVar", "r", "MathOp", ref_("MO_MIN")])]);

        yield return ("while", [
            BbTest.SetVar("i", 0),
            BbTest.Block("BBWhile", ["Src1Var", "i", "Value2", 3, "CompareOp", ref_("CO_LESS_THAN")], BbTest.Increment("i"), BbTest.Block("BBBreakExecution", [])),
            BbTest.SetVar("after", 1),
        ]);
        yield return ("while-tables", [
            BbTest.SetVar("lim", 3, "InstanceVars"), BbTest.SetVar("i", 0, "SpellVars"),
            BbTest.Block("BBWhile", ["Src1Var", "i", "Src1VarTable", "SpellVars", "Src2Var", "lim", "Src2VarTable", "InstanceVars", "CompareOp", ref_("CO_LESS_THAN")],
                BbTest.Block("BBMath", ["Src1Var", "i", "Src1VarTable", "SpellVars", "Src2Value", 1, "DestVar", "i", "DestVarTable", "SpellVars", "MathOp", ref_("MO_ADD")])),
        ]);

        yield return ("break-and-else", [
            BbTest.SetVar("x", 1),
            BbTest.If("x", "CO_EQUAL", 1, BbTest.SetVar("a", 1), BbTest.Block("BBBreakExecution", []), BbTest.SetVar("b", 1)),
            BbTest.SetVar("c", 1),
            BbTest.Block("BBElse", [], BbTest.SetVar("d", 1)),
        ]);
        yield return ("else-chain", [
            BbTest.SetVar("x", 2),
            BbTest.If("x", "CO_EQUAL", 1, BbTest.SetVar("branch", "if")),
            BbTest.Block("BBElseIf", ["Src1Var", "x", "Value2", 2, "CompareOp", ref_("CO_EQUAL")], BbTest.SetVar("branch", "elseif")),
            BbTest.Block("BBElseIf", ["Src1Var", "x", "Value2", 2, "CompareOp", ref_("CO_EQUAL")], BbTest.SetVar("branch2", "elseif2")),
            BbTest.Block("BBElse", [], BbTest.SetVar("branch3", "else")),
            BbTest.Block("BBElse", [], BbTest.SetVar("lone", "else2")),
        ]);

        yield return ("by-level", [
            BbTest.Block("BBSetVarInTable", ["DestVar", "d", "DestVarTable", "InstanceVars", "SrcValueByLevel", table, "SrcValue", 0]),
            BbTest.Block("BBSetReturnValue", ["SrcValueByLevel", table]),
            BbTest.Block("BBSetReturnValue", ["SrcVar", "d", "SrcVarTable", "InstanceVars"]),
            BbTest.Block("BBIncStat", ["Stat", ref_("IncFlatArmorMod"), "TargetVar", "Owner", "Delta", 1, "DeltaByLevel", table, "DeltaVar", "d", "DeltaVarTable", "InstanceVars"]),
            BbTest.Block("BBIncPermanentStat", ["Stat", ref_("IncPermanentFlatHPPoolMod"), "Delta", 2, "DeltaByLevel", table]),
            BbTest.Block("BBIncStat", ["Stat", ref_("IncPercentMovementSpeedMod"), "TargetVar", "Target", "DeltaVar", "created", "DeltaVarTable", "FreshTable"]),
        ]);

        yield return ("stats-and-status", [
            BbTest.Block("BBGetStat", ["Stat", ref_("GetFlatMagicDamageMod"), "TargetVar", "Owner", "DestVar", "ap"]),
            BbTest.Block("BBGetStat", ["Stat", ref_("GetBaseAttackDamage"), "DestVar", "ad", "DestVarTable", "NoSuchTable"]),
            BbTest.Block("BBGetStatus", ["Status", ref_("IsMoving"), "TargetVar", "Owner", "DestVar", "moving", "DestVarTable", "CreatedByStatus"]),
            BbTest.Block("BBGetCastInfo", ["Info", ref_("GetCastSpellLevelPlusOne"), "DestVar", "rank"]),
            BbTest.Block("BBGetCastInfo", ["Info", ref_("GetSpellName"), "TargetVar", "Owner", "DestVar", "spellName"]),
            BbTest.Block("BBSetStatus", ["Status", ref_("SetCanMove"), "TargetVar", "Owner", "SrcValue", false]),
            BbTest.SetVar("flag", true, "InstanceVars"),
            BbTest.Block("BBSetStatus", ["Status", ref_("SetStealthed"), "TargetVar", "Target", "SrcVar", "flag", "SrcTable", "InstanceVars"]),
            BbTest.Block("BBSetStatus", ["Status", ref_("SetNoRender"), "TargetVar", "Target", "SrcVar", "flag", "SrcTable", "CreatedBySetStatus"]),
            BbTest.Block("BBGetLevel", ["TargetVar", "Attacker", "DestVar", "lvl"]),
            BbTest.Block("BBGetLevel", ["DestVar", "ownLvl"]),
            BbTest.Block("BBGetArmor", ["TargetVar", "Attacker", "DestVar", "armor", "DestVarTable", "InstanceVars"]),
            BbTest.Block("BBGetSpellBlock", ["DestVar", "mr"]),
            BbTest.Block("BBGetTeamID", ["TargetVar", "Target", "DestVar", "team", "DestVarTable", "SpellVars"]),
            BbTest.Block("BBGetTotalAttackDamage", ["TargetVar", "Owner", "DestVar", "tad"]),
            BbTest.Block("BBGetUnitSkinName", ["TargetVar", "Owner", "DestVar", "skin"]),
            BbTest.Block("BBGetUnitSkinName", ["DestVar", "skinAbsent"]),
            BbTest.Block("BBGetPAROrHealth", ["Function", ref_("GetHealth"), "OwnerVar", "Owner", "PARType", ref_("PAR_MANA"), "DestVar", "hp"]),
            BbTest.Block("BBGetPAROrHealth", ["Function", ref_("GetPAR"), "OwnerVar", "Target", "PARType", ref_("PAR_ENERGY"), "DestVar", "par", "DestVarTable", "CreatedByPar"]),
            BbTest.Block("BBGetGold", ["DestVar", "gold", "TargetVar", "Owner"]),
            BbTest.Block("BBGetTime", ["DestVar", "t1"]),
            BbTest.Block("BBGetTime", ["DestVar", "t2", "DestVarTable", "InstanceVars"]),
        ]);

        yield return ("buffs", [
            BbTest.Block("BBIfHasBuffOfType", ["TargetVar", "Target", "BuffType", ref_("BUFF_Stun")], BbTest.SetVar("typed", 1)),
            BbTest.Block("BBIfHasBuffOfType", ["BuffType", ref_("BUFF_Slow")], BbTest.SetVar("typedDefault", 1)),
            BbTest.Block("BBElse", [], BbTest.SetVar("typedElse", 1)),
            BbTest.Block("BBIfHasBuff", ["BuffName", "Pyromania"], BbTest.SetVar("has", 1)),
            BbTest.Block("BBIfHasBuff", ["OwnerVar", "Target", "AttackerVar", "Other1", "BuffName", "Pyromania"], BbTest.SetVar("has2", 1)),
            BbTest.Block("BBIfNotHasBuff", ["BuffName", "Pyromania_marker"], BbTest.SetVar("hasNot", 1)),
            BbTest.Block("BBIfNotHasBuff", ["OwnerVar", "Attacker", "CasterVar", "Other2", "BuffName", "Stun"], BbTest.SetVar("hasNot2", 1)),
            BbTest.Block("BBIfPARTypeEquals", ["PARType", ref_("PAR_MANA")], BbTest.SetVar("mana", 1)),
            BbTest.Block("BBIfPARTypeEquals", ["OwnerVar", "Target", "PARType", ref_("PAR_ENERGY")], BbTest.SetVar("energy", 1)),
            BbTest.Block("BBIfPARTypeNotEquals", ["PARType", ref_("PAR_MANA")], BbTest.SetVar("notMana", 1)),
            BbTest.Block("BBIfPARTypeNotEquals", ["OwnerVar", "Target", "PARType", ref_("PAR_ENERGY")], BbTest.SetVar("notEnergy", 1)),
            BbTest.Block("BBGetBuffCountFromCaster", ["TargetVar", "Owner", "CasterVar", "Caster", "BuffName", "Pyromania", "DestVar", "fromCaster"]),
            BbTest.Block("BBGetBuffCountFromAll", ["TargetVar", "Owner", "BuffName", "Pyromania", "DestVar", "fromAll", "DestVarTable", "CreatedByBuffs"]),
            BbTest.Block("BBSpellBuffRemoveStacks", ["TargetVar", "Owner", "AttackerVar", "Attacker", "BuffName", "Pyromania", "NumStacks", 3]),
            BbTest.Block("BBSpellBuffRemoveStacks", ["TargetVar", "Owner", "AttackerVar", "Attacker", "BuffName", "Pyromania", "NumStacks", 0]),
            BbTest.Block("BBBreakSpellShields", ["TargetVar", "Target"]),
        ]);

        yield return ("spell-slots", [
            BbTest.SetVar("slot", 2, "SpellVars"),
            BbTest.Block("BBGetSlotSpellInfo", ["Function", ref_("GetSlotSpellLevel"), "OwnerVar", "Owner", "SpellSlotValue", 1, "SpellbookType", ref_("SPELLBOOK_CHAMPION"), "SlotType", ref_("SpellSlots"), "DestVar", "lvl"]),
            BbTest.Block("BBGetSlotSpellInfo", ["Function", ref_("GetSlotSpellName"), "OwnerVar", "Owner", "SpellSlotVar", "slot", "SpellSlotVarTable", "SpellVars", "SpellbookType", ref_("SPELLBOOK_SUMMONER"), "SlotType", ref_("SpellSlots"), "DestVar", "name", "DestVarTable", "InstanceVars"]),
            BbTest.Block("BBGetSlotSpellInfo", ["Function", ref_("GetSlotSpellCooldownTime"), "OwnerVar", "Owner", "SpellSlotVar", "slot", "SpellbookType", ref_("SPELLBOOK_CHAMPION"), "SlotType", ref_("SpellSlots"), "DestVar", "cd"]),
            BbTest.SetVar("cdv", 4.5, "InstanceVars"),
            BbTest.Block("BBSetSlotSpellCooldownTime", ["OwnerVar", "Owner", "SpellSlotValue", 3, "SpellbookType", ref_("SPELLBOOK_CHAMPION"), "SlotType", ref_("SpellSlots"), "SrcValue", 9]),
            BbTest.Block("BBSetSlotSpellCooldownTime", ["OwnerVar", "Owner", "SpellSlotVar", "slot", "SpellSlotVarTable", "SpellVars", "SpellbookType", ref_("SPELLBOOK_CHAMPION"), "SlotType", ref_("SpellSlots"), "SrcVar", "cdv", "SrcVarTable", "InstanceVars"]),
            BbTest.Block("BBSetSpellCastRange", ["NewRange", 700]),
            BbTest.Block("BBSetCanCastWhileDisabled", ["CanCast", true]),
        ]);

        yield return ("orders-and-geometry", [
            BbTest.Block("BBIssueOrder", ["WhomToOrderVar", "Owner", "TargetOfOrderVar", "Target", "Order", ref_("AI_ATTACKTO")]),
            BbTest.Block("BBDistanceBetweenObjects", ["ObjectVar1", "Owner", "ObjectVar2", "Target", "DestVar", "d1"]),
            BbTest.Block("BBGetTime", ["DestVar", "unusedTime"]),
            BbTest.Block("BBSetVarInTable", ["DestVar", "p1", "DestVarTable", "InstanceVars", "SrcValue", 1]),
            BbTest.Block("BBDistanceBetweenObjectAndPoint", ["ObjectVar", "Owner", "PointVar", "pos", "PointVarTable", "CreatedByPoint", "DestVar", "d2", "DestVarTable", "InstanceVars"]),
            BbTest.Block("BBDistanceBetweenPoints", ["Point1Var", "a", "Point1VarTable", "PointsA", "Point2Var", "b", "Point2VarTable", "PointsB", "DestVar", "d3"]),
            BbTest.Block("BBTeleportToPoint", ["OwnerVar", "Owner", "X", 100, "Y", 5, "Z", 200]),
            BbTest.SetVar("tx", 11, "InstanceVars"), BbTest.SetVar("tz", 33, "InstanceVars"),
            BbTest.Block("BBTeleportToPoint", ["OwnerVar", "Target", "XVar", "tx", "XVarTable", "InstanceVars", "Y", 0, "ZVar", "tz", "ZVarTable", "InstanceVars", "CastPositionName", "old"]),
            BbTest.Block("BBDefUpdateAura", ["CenterVar", "Owner", "Range", 600, "UnitScan", ref_("UNITSCAN_Enemies"), "BuffName", "AuraBuff"]),
            BbTest.Block("BBDefUpdateAura", ["CenterTable", "SpellVars", "CenterVar", "slot", "Range", 100, "UnitScan", ref_("UNITSCAN_Friends"), "BuffName", "AuraBuff2"]),
            BbTest.Block("BBReincarnateHero", ["TargetVar", "Owner"]),
            BbTest.Block("BBReincarnateHero", ["TargetTable", "InstanceVars", "TargetVar", "tx"]),
            BbTest.Block("BBSetScaleSkinCoef", ["OwnerVar", "Owner", "Scale", 1.5]),
            BbTest.Block("BBSetScaleSkinCoef", ["ScaleVar", "tx", "ScaleVarTable", "InstanceVars", "Scale", 9]),
            BbTest.Block("BBSetScaleSkinCoef", ["ScaleVar", "tx", "Scale", 2]),
        ]);

        yield return ("knockback", [
            BbTest.Block("BBKnockback", ["TargetVar", "Target", "AttackerVar", "Attacker", "Amount", 300, "KnockBackDuration", 1]),
            BbTest.Block("BBKnockback", ["TargetVar", "Target", "AttackerVar", "Attacker", "Amount", 300, "KnockBackDuration", 1]),
            BbTest.Block("BBKnockback", ["TargetVar", "Target", "AttackerVar", "Attacker", "Amount", 300, "KnockBackDuration", 1]),
        ]);
        yield return ("knockback-var", [
            BbTest.SetVar("amt", 250, "SpellVars"),
            BbTest.Block("BBKnockback", ["TargetVar", "Target", "AttackerVar", "Attacker", "AmountVar", "amt", "AmountVarTable", "SpellVars", "KnockBackDuration", 2]),
            BbTest.Block("BBKnockback", ["TargetVar", "Target", "AttackerVar", "Attacker", "AmountVar", "amt", "AmountVarTable", "SpellVars", "KnockBackDuration", 2]),
        ]);

        yield return ("say-and-alert", [
            BbTest.SetVar("number", 7.25), BbTest.SetVar("text", "hello"), BbTest.SetVar("flag", true),
            BbTest.Block("BBSay", ["ToSay", "value: ", "SrcVar", "number"]),
            BbTest.Block("BBSay", ["ToSay", "owner: ", "SrcVar", "text", "OwnerVar", "Target"]),
            BbTest.Block("BBDebugSay", ["ToSay", "debug "]),
            BbTest.Block("BBDebugSay", ["ToSay", "bool ", "SrcVar", "flag"]),
            BbTest.Block("BBAlert", ["ToAlert", "alert: ", "SrcVar", "number"]),
            BbTest.Block("BBAlert", ["ToAlert", "plain"]),
            BbTest.Block("BBAlert", ["ToAlert", "bad: ", "SrcVar", "flag"]), // concatenating a boolean is a Lua error
            BbTest.Block("BBSay", ["ToSay", "never"]),
        ]);

        yield return ("periodic", [
            BbTest.Block("BBExecutePeriodically", ["TimeBetweenExecutions", 1, "TrackTimeVar", "t", "TrackTimeVarTable", "InstanceVars", "ExecuteImmediately", true], BbTest.Increment("a")),
            BbTest.Block("BBExecutePeriodically", ["TimeBetweenExecutions", 1, "TrackTimeVar", "t", "TrackTimeVarTable", "InstanceVars", "ExecuteImmediately", true], BbTest.Increment("a")),
            BbTest.Block("BBExecutePeriodically", ["TimeBetweenExecutions", 1, "TrackTimeVar", "t", "TrackTimeVarTable", "InstanceVars", "ExecuteImmediately", true], BbTest.Increment("a")),
            BbTest.Block("BBExecutePeriodicallyReset", ["TrackTimeVar", "t", "TrackTimeVarTable", "InstanceVars"]),
            BbTest.SetVar("tick", 0.5, "SpellVars"),
            BbTest.Block("BBExecutePeriodically", ["TimeBetweenExecutions", 9, "TickTimeVar", "tick", "TickTimeVarTable", "SpellVars", "TrackTimeVar", "u"], BbTest.Increment("b")),
            BbTest.Block("BBExecutePeriodically", ["TimeBetweenExecutions", 9, "TickTimeVar", "tick", "TickTimeVarTable", "SpellVars", "TrackTimeVar", "u"], BbTest.Increment("b")),
            BbTest.Block("BBExecutePeriodically", ["TimeBetweenExecutions", 9, "TickTimeVar", "tick", "TickTimeVarTable", "SpellVars", "TrackTimeVar", "u"], BbTest.Increment("b")),
            BbTest.Block("BBExecutePeriodically", ["TimeBetweenExecutions", 9, "TickTimeVar", "missing", "TrackTimeVar", "v"], BbTest.Increment("c")),
            BbTest.Block("BBExecutePeriodically", ["TrackTimeVar", "w"], BbTest.Increment("d")), // no period: arithmetic on nil
        ]);

        yield return ("errors", [
            BbTest.Block("BBConditionallyExecute", []),
        ]);
        yield return ("errors-nil-key", [
            BbTest.SetVar("fine", 1),
            BbTest.Block("BBSetVarInTable", ["SrcValue", 1]), // no DestVar: writing a nil key
            BbTest.SetVar("never", 1),
        ]);
        yield return ("errors-compare", [
            BbTest.SetVar("s", "text"),
            BbTest.Block("BBIf", ["Src1Var", "s", "Value2", 1, "CompareOp", ref_("CO_LESS_THAN")], BbTest.SetVar("never", 1)),
        ]);
        yield return ("errors-no-op", [
            BbTest.Block("BBIf", ["Src1Var", "s", "Value2", 1], BbTest.SetVar("never", 1)),
        ]);
        yield return ("errors-level", [
            BbTest.Block("BBIncStat", ["Stat", ref_("IncFlatArmorMod"), "DeltaByLevel", new BbValue.Table([new BbValue.Number(1)], [])]),
        ]);
        yield return ("required-vars", [
            BbTest.Block("BBRequireVar", ["RequiredVar", "x"]),
            BbTest.SetVar("ok", 1),
        ]);
    }
}
