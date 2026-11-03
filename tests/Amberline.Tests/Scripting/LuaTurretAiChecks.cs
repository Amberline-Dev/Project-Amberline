using Amberline.Data;
using Amberline.Scripting.Lua;
// Amberline.Data has a LuaValue record for the listing side; the runtime one wins here.
using LuaValue = Amberline.Scripting.Lua.LuaValue;

namespace Amberline.Tests;

/// <summary>
/// Turret.luaobj's nine entry points, run from their shipped bytecode against recording stubs.
/// The expected call sequences are read straight off the bytecode listing of Turret.luaobj
/// (proto 0.0 OnInit through 0.8 HaltAI), so this pins the interpreter's control flow and the
/// natives those entry points touch. Every entry point reads <c>GetState()</c> once into a local
/// and compares that copy, so one call per invocation; the AI state ids are the host's
/// <c>Reconstruction:</c> ones (AI_HALTED 0, AI_HARDIDLE 2, AI_TAUNTED 5, AI_HARDIDLE_ATTACKING 30,
/// STOPREASON_IMMEDIATELY 100, STOPREASON_MOVING 101, STOPREASON_TARGET_LOST 102).
/// </summary>
public static class LuaTurretAiChecks
{
    [Check("scripting.lua-turret-ai")]
    public static void TurretEntryPointsMakeTheExpectedNativeCalls()
    {
        var paths = GameDataPaths.Default;
        var host = new LuaRecordingHost(paths);
        var turret = LuaObjectReader.ParseFile(Path.Combine(paths.Root, "DATA", "Scripts", "Turret.luaobj"));
        host.CoverGlobals(turret);
        host.State.DoChunk(turret);

        // The main chunk only installs the nine closures; it calls no native.
        foreach (var name in new[]
                 {
                     "OnInit", "OnTargetLost", "OnCallForHelp", "OnTauntBegin", "OnTauntEnd",
                     "OnCanMove", "OnCanAttack", "TimerFindEnemies", "HaltAI",
                 })
            TestAssert.Equal(LuaType.Function, host.State.GetGlobal(name).Type, $"Turret defines {name}");

        // ---- OnInit: hard idle, then the 0.15 s repeating scan ----

        host.Clear();
        host.Stub("GetState", N(2));                        // AI_HARDIDLE
        host.Stub("FindTargetInAcR");
        host.Stub("TargetInAttackRange");
        host.Stub("GetTarget");
        host.Stub("GetTauntTarget");
        host.State.CallGlobal("OnInit");
        TestAssert.Equal(new[]
        {
            "SetState(2)",
            "InitTimer('TimerFindEnemies', 0.15, true)",
        }, host.Calls.ToArray(), "OnInit starts in HARDIDLE and arms the repeating scan");

        // ---- HaltAI: stop the scan, stop the swing, HALTED ----

        host.Clear();
        host.State.CallGlobal("HaltAI");
        TestAssert.Equal(new[]
        {
            "StopTimer('TimerFindEnemies')",
            "TurnOffAutoAttack(100)",
            "NetSetState(0)",
        }, host.Calls.ToArray(), "HaltAI stops the timer, the swing and the state machine");

        // ---- OnTargetLost: a replacement is taken only while ATTACKING or TAUNTED ----

        host.Clear();
        host.Stub("GetState", N(0));                        // AI_HALTED
        var halted = host.State.CallGlobal("OnTargetLost");
        TestAssert.Equal(new[] { "GetState()" }, host.Calls.ToArray(), "OnTargetLost returns at once when halted");
        TestAssert.Equal(0, halted.Length, "a halted OnTargetLost returns nothing");

        host.Clear();
        host.Stub("GetState", N(30));                       // AI_HARDIDLE_ATTACKING
        host.Stub("FindTargetInAcR");                       // the replacement is gone too
        var lost = host.State.CallGlobal("OnTargetLost");
        TestAssert.Equal(new[]
        {
            "GetState()",
            "FindTargetInAcR()",
            "NetSetState(2)",
        }, host.Calls.ToArray(), "losing the last target returns the turret to HARDIDLE");
        TestAssert.Equal(LuaType.Boolean, lost[0].Type, "OnTargetLost reports true on every non-halted path");
        TestAssert.True(lost[0].AsBoolean, "OnTargetLost reports true on every non-halted path");

        host.Clear();
        host.Stub("GetState", N(30));
        host.Stub("FindTargetInAcR", N(7));
        host.State.CallGlobal("OnTargetLost");
        TestAssert.Equal(new[]
        {
            "GetState()",
            "FindTargetInAcR()",
            "NetSetState(30)",
            "SetTarget(7)",
        }, host.Calls.ToArray(), "an ATTACKING turret takes the replacement at once");

        host.Clear();
        host.Stub("GetState", N(5));                        // AI_TAUNTED
        host.Stub("FindTargetInAcR", N(7));
        host.State.CallGlobal("OnTargetLost");
        TestAssert.Equal(new[]
        {
            "GetState()",
            "FindTargetInAcR()",
            "NetSetState(30)",
            "SetTarget(7)",
        }, host.Calls.ToArray(), "a TAUNTED turret takes the replacement and leaves the taunt state");

        // The state gate, whole: the same search result is discarded outside those two states, so
        // SetTarget is never called and the turret stays in HARDIDLE with no target.
        host.Clear();
        host.Stub("GetState", N(2));                        // AI_HARDIDLE
        host.Stub("FindTargetInAcR", N(7));
        host.State.CallGlobal("OnTargetLost");
        TestAssert.Equal(new[]
        {
            "GetState()",
            "FindTargetInAcR()",
            "NetSetState(2)",
        }, host.Calls.ToArray(), "a HARDIDLE turret discards the search result");

        // ---- OnCallForHelp(self, aggressor): the aggressor, unless taunted ----

        host.Clear();
        host.Stub("GetState", N(0));
        host.State.CallGlobal("OnCallForHelp", N(1), N(42));
        TestAssert.Equal(new[] { "GetState()" }, host.Calls.ToArray(), "OnCallForHelp returns at once when halted");

        host.Clear();
        host.Stub("GetState", N(2));
        host.State.CallGlobal("OnCallForHelp", N(1), LuaValue.Nil);
        TestAssert.Equal(new[] { "GetState()" }, host.Calls.ToArray(), "a missing aggressor is ignored");

        host.Clear();
        host.Stub("GetState", N(2));                        // AI_HARDIDLE
        host.State.CallGlobal("OnCallForHelp", N(1), N(42));
        TestAssert.Equal(new[]
        {
            "GetState()",
            "NetSetState(30)",
            "SetTarget(42)",
        }, host.Calls.ToArray(), "a HARDIDLE turret takes the aggressor at once");

        host.Clear();
        host.Stub("GetState", N(30));
        host.State.CallGlobal("OnCallForHelp", N(1), N(42));
        TestAssert.Equal(new[]
        {
            "GetState()",
            "NetSetState(30)",
            "SetTarget(42)",
        }, host.Calls.ToArray(), "an ATTACKING turret drops its target for the aggressor");

        host.Clear();
        host.Stub("GetState", N(5));                        // AI_TAUNTED
        host.State.CallGlobal("OnCallForHelp", N(1), N(42));
        TestAssert.Equal(new[] { "GetState()" }, host.Calls.ToArray(), "a TAUNTED turret ignores the call for help");

        // ---- OnTauntBegin / OnTauntEnd ----

        host.Clear();
        host.Stub("GetState", N(2));
        host.Stub("GetTauntTarget");                        // nil: nobody taunted us
        host.State.CallGlobal("OnTauntBegin");
        TestAssert.Equal(new[] { "GetState()", "GetTauntTarget()" }, host.Calls.ToArray(),
            "OnTauntBegin leaves the state alone without a taunt target");

        host.Clear();
        host.Stub("GetState", N(2));
        host.Stub("GetTauntTarget", N(42));
        host.State.CallGlobal("OnTauntBegin");
        TestAssert.Equal(new[]
        {
            "GetState()",
            "GetTauntTarget()",
            "NetSetState(5)",
            "SetTarget(42)",
        }, host.Calls.ToArray(), "OnTauntBegin closes on the taunter in AI_TAUNTED");

        host.Clear();
        host.Stub("GetState", N(2));
        host.Stub("GetTauntTarget", N(42));
        host.Stub("FindTargetInAcR");
        host.Stub("TargetInAttackRange");
        host.Stub("GetTarget");
        host.State.CallGlobal("OnTauntEnd");
        TestAssert.Equal(new[]
        {
            "GetState()",
            "GetTauntTarget()",
            "NetSetState(30)",
            "SetTarget(42)",
        }, host.Calls.ToArray(), "OnTauntEnd keeps the taunter and goes to HARDIDLE_ATTACKING");

        // With the taunt gone it drops to HARDIDLE and runs this same chunk's TimerFindEnemies at
        // once — whose own native calls land in the middle of the record — which is what keeps the
        // turret shooting after a taunt expires.
        host.Clear();
        host.Stub("GetState", N(2));
        host.Stub("GetTauntTarget");                        // nil: the taunt expired
        host.Stub("FindTargetInAcR", N(7));
        host.State.CallGlobal("OnTauntEnd");
        TestAssert.Equal(new[]
        {
            "GetState()",
            "GetTauntTarget()",
            "NetSetState(2)",
            "GetState()",
            "FindTargetInAcR()",
            "NetSetState(30)",
            "SetTarget(7)",
        }, host.Calls.ToArray(), "OnTauntEnd without a taunter re-selects through TimerFindEnemies");

        // ---- OnCanMove / OnCanAttack: back to HARDIDLE and an immediate scan ----

        foreach (var hook in new[] { "OnCanMove", "OnCanAttack" })
        {
            host.Clear();
            host.Stub("GetState", N(0));
            host.State.CallGlobal(hook);
            TestAssert.Equal(new[] { "GetState()" }, host.Calls.ToArray(), $"{hook} returns at once when halted");

            host.Clear();
            host.Stub("GetState", N(2));
            host.Stub("FindTargetInAcR", N(7));
            host.State.CallGlobal(hook);
            TestAssert.Equal(new[]
            {
                "GetState()",
                "NetSetState(2)",
                "GetState()",
                "FindTargetInAcR()",
                "NetSetState(30)",
                "SetTarget(7)",
            }, host.Calls.ToArray(), $"{hook} drops to HARDIDLE and scans at once");
        }

        // ---- TimerFindEnemies: the whole scan, on the state it entered with ----

        host.Clear();
        host.Stub("GetState", N(0));
        host.State.CallGlobal("TimerFindEnemies");
        TestAssert.Equal(new[] { "GetState()" }, host.Calls.ToArray(),
            "TimerFindEnemies returns at once when halted");

        host.Clear();
        host.Stub("GetState", N(1));                        // AI_IDLE: not one of its two entries
        host.State.CallGlobal("TimerFindEnemies");
        TestAssert.Equal(new[] { "GetState()" }, host.Calls.ToArray(),
            "TimerFindEnemies only runs for HARDIDLE, ATTACKING and TAUNTED");

        host.Clear();
        host.Stub("GetState", N(2));
        host.Stub("FindTargetInAcR");                       // empty scan
        host.State.CallGlobal("TimerFindEnemies");
        TestAssert.Equal(new[]
        {
            "GetState()",
            "FindTargetInAcR()",
            "TurnOffAutoAttack(102)",
        }, host.Calls.ToArray(), "an empty HARDIDLE scan turns the swing off with TARGET_LOST");

        // Batch 56 pinned this: the acquisition tick sets the state and the target but the entry
        // state still reads HARDIDLE when the gate at the end runs, so TurnOnAutoAttack is not
        // called and the swing starts on the NEXT 0.15 s tick.
        host.Clear();
        host.Stub("GetState", N(2));
        host.Stub("FindTargetInAcR", N(7));
        host.State.CallGlobal("TimerFindEnemies");
        TestAssert.Equal(new[]
        {
            "GetState()",
            "FindTargetInAcR()",
            "NetSetState(30)",
            "SetTarget(7)",
        }, host.Calls.ToArray(), "the acquisition tick does not start the swing");

        host.Clear();
        host.Stub("GetState", N(30));
        host.Stub("TargetInAttackRange", LuaValue.Of(true));
        host.Stub("GetTarget", N(7));
        host.State.CallGlobal("TimerFindEnemies");
        TestAssert.Equal(new[]
        {
            "GetState()",
            "TargetInAttackRange()",
            "GetTarget()",
            "TurnOnAutoAttack(7)",
        }, host.Calls.ToArray(), "an in-range ATTACKING scan turns the swing on at GetTarget()");

        // Out of attack range is not a target loss: the state drops to HARDIDLE and the swing
        // stops with MOVING, but SetTarget is never called so the target id stays.
        host.Clear();
        host.Stub("GetState", N(30));
        host.Stub("TargetInAttackRange");                   // falsy: out of range
        host.State.CallGlobal("TimerFindEnemies");
        TestAssert.Equal(new[]
        {
            "GetState()",
            "TargetInAttackRange()",
            "NetSetState(2)",
            "TurnOffAutoAttack(101)",
        }, host.Calls.ToArray(), "an out-of-range scan drops to HARDIDLE and keeps the target id");

        host.Clear();
        host.Stub("GetState", N(5));                        // AI_TAUNTED
        host.Stub("TargetInAttackRange", LuaValue.Of(true));
        host.Stub("GetTarget", N(42));
        host.State.CallGlobal("TimerFindEnemies");
        TestAssert.Equal(new[]
        {
            "GetState()",
            "TargetInAttackRange()",
            "GetTarget()",
            "TurnOnAutoAttack(42)",
        }, host.Calls.ToArray(), "a TAUNTED scan keeps swinging at the taunter");
    }

    private static LuaValue N(double value) => LuaValue.Of(value);
}
