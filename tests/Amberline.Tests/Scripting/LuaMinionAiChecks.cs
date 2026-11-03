using Amberline.Data;
using Amberline.Scripting.Lua;
// Amberline.Data has a LuaValue record for the listing side; the runtime one wins here.
using LuaValue = Amberline.Scripting.Lua.LuaValue;

namespace Amberline.Tests;

/// <summary>
/// Minion.luaobj's AI entry points, run from their shipped bytecode against recording stubs.
/// The expected call sequences are read straight off the bytecode listing of the prototypes the main
/// chunk installs as globals (proto 0.0 OnTauntBegin, 0.2 TimerFindEnemies, 0.3 FindTargetOrMove),
/// so this pins both the interpreter's control flow and the natives those entry points touch.
/// The AI state ids they pass along are the host's <c>Reconstruction:</c> ones.
/// </summary>
public static class LuaMinionAiChecks
{
    [Check("scripting.lua-minion-ai")]
    public static void MinionEntryPointsMakeTheExpectedNativeCalls()
    {
        var paths = GameDataPaths.Default;
        var host = new LuaRecordingHost(paths);
        var minion = LuaObjectReader.ParseFile(Path.Combine(paths.Root, "DATA", "Scripts", "Minion.luaobj"));
        host.CoverGlobals(minion);
        host.State.DoChunk(minion);

        // The main chunk dofiles minionLib and then installs its five entry points.
        TestAssert.True(host.Calls.Any(call => call.StartsWith("dofile(", StringComparison.Ordinal)),
            "Minion's main chunk opens minionLib");
        foreach (var name in new[] { "OnTauntBegin", "OnTauntEnd", "TimerFindEnemies", "FindTargetOrMove", "TimerMoveForward" })
            TestAssert.Equal(LuaType.Function, host.State.GetGlobal(name).Type, $"Minion defines {name}");
        TestAssert.Equal(2500.0, host.State.GetGlobal("MAX_ENGAGE_DISTANCE").AsNumber, "MAX_ENGAGE_DISTANCE");
        TestAssert.Equal(500.0, host.State.GetGlobal("FEAR_WANDER_DISTANCE").AsNumber, "FEAR_WANDER_DISTANCE");
        TestAssert.Equal(0.25, host.State.GetGlobal("DELAY_FIND_ENEMIES").AsNumber, "DELAY_FIND_ENEMIES");

        // ---- OnTauntBegin: halt guard, then the taunt target ----

        host.Clear();
        host.Stub("GetState", N(0));                        // AI_HALTED
        host.State.CallGlobal("OnTauntBegin");
        TestAssert.Equal(new[] { "GetState()" }, host.Calls.ToArray(), "OnTauntBegin returns at once when halted");

        host.Clear();
        host.Stub("GetState", N(1));                        // anything else
        host.Stub("GetTauntTarget");                        // nil: nobody taunted us
        host.State.CallGlobal("OnTauntBegin");
        TestAssert.Equal(new[] { "GetState()", "GetTauntTarget()" }, host.Calls.ToArray(),
            "OnTauntBegin leaves the state alone without a taunt target");

        host.Clear();
        host.Stub("GetState", N(1));
        host.Stub("GetTauntTarget", N(42));
        host.State.CallGlobal("OnTauntBegin");
        TestAssert.Equal(new[]
        {
            "GetState()",
            "GetTauntTarget()",
            "SetStateAndCloseToTarget(5, 42)",
            "StopTimer('TimerAntiKite')",
        }, host.Calls.ToArray(), "OnTauntBegin closes on the taunt target and stops the anti-kite timer");

        // ---- FindTargetOrMove: halt guard, then find a target or walk the lane ----

        host.Clear();
        host.Stub("GetState", N(1));
        host.Stub("FindTargetInAcR");                       // nothing in range
        host.State.CallGlobal("FindTargetOrMove");
        TestAssert.Equal(new[]
        {
            "GetState()",
            "FindTargetInAcR()",
            "SetStateAndMoveToForwardNav(3)",
            "StopTimer('TimerAntiKite')",
        }, host.Calls.ToArray(), "FindTargetOrMove marches on when it finds nothing");

        host.Clear();
        host.Stub("GetState", N(1));
        host.Stub("FindTargetInAcR", N(7));
        host.Stub("LastAutoAttackFinished", LuaValue.Of(false));
        host.State.CallGlobal("FindTargetOrMove");
        TestAssert.Equal(new[]
        {
            "GetState()",
            "FindTargetInAcR()",
            "LastAutoAttackFinished()",
            "InitTimer('TimerFindEnemies', 0.25, true)",
        }, host.Calls.ToArray(), "FindTargetOrMove waits out an unfinished auto-attack");

        host.Clear();
        host.Stub("GetState", N(1));
        host.Stub("FindTargetInAcR", N(7));
        host.Stub("LastAutoAttackFinished", LuaValue.Of(true));
        host.State.CallGlobal("FindTargetOrMove");
        TestAssert.Equal(new[]
        {
            "GetState()",
            "FindTargetInAcR()",
            "LastAutoAttackFinished()",
            "SetStateAndCloseToTarget(4, 7)",
            "ResetAndStartTimer('TimerAntiKite')",
        }, host.Calls.ToArray(), "FindTargetOrMove engages as soon as the swing is over");

        // ---- TimerFindEnemies: the attack-move and taunted arms of the state machine ----

        host.Clear();
        host.Stub("GetState", N(3));                        // AI_ATTACKMOVESTATE
        host.Stub("FindTargetInAcR");                       // the target walked out of range
        host.State.CallGlobal("TimerFindEnemies");
        TestAssert.Equal(new[]
        {
            "GetState()",
            "FindTargetInAcR()",
            "TurnOffAutoAttack(102)",
        }, host.Calls.ToArray(), "TimerFindEnemies drops the swing when the target is lost");

        // The taunted arm passes GetTauntTarget's result through a multret CALL into
        // SetStateAndCloseToTarget, probes GetTarget, and then runs FindTargetOrMove — which is
        // this same chunk's global, so its own native calls land in the middle of the record —
        // before the attack-range check.
        host.Clear();
        host.Stub("GetState", N(5));                        // AI_TAUNTED
        host.Stub("GetTauntTarget", N(42));
        host.Stub("GetTarget");                             // nothing to chase yet
        host.Stub("FindTargetInAcR");
        host.Stub("TargetInAttackRange");
        host.Stub("TargetInCancelAttackRange");
        host.State.CallGlobal("TimerFindEnemies");
        TestAssert.Equal(new[]
        {
            "GetState()",
            "GetTauntTarget()",
            "GetTauntTarget()",
            "SetStateAndCloseToTarget(5, 42)",
            "GetTarget()",
            "GetState()",
            "FindTargetInAcR()",
            "SetStateAndMoveToForwardNav(3)",
            "StopTimer('TimerAntiKite')",
            "TargetInAttackRange()",
            "TargetInCancelAttackRange()",
        }, host.Calls.ToArray(), "TimerFindEnemies holds the taunt and keeps looking for a target");
    }

    private static LuaValue N(double value) => LuaValue.Of(value);
}
