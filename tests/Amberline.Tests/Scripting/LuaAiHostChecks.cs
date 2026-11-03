using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.Lua;
using Amberline.Simulation.Ai;
using LuaValue = Amberline.Scripting.Lua.LuaValue;

namespace Amberline.Tests;

/// <summary>
/// The Lua AI host batch 141 put every turret through: one script instance per AI unit, the
/// engine's events dispatched into that unit's callbacks, and its named timers. Everything the
/// host does with the world goes through <see cref="ILuaAiEngine"/>, so the doubles here are that
/// interface alone — the point is the host's own sequencing, not the services underneath.
/// The shipped DATA/Scripts/Turret.luaobj supplies the callbacks; the one host behaviour it does
/// not reach (one-shot timers, <c>ResetAndStartTimer</c>, a script that raises) is driven from a
/// hand-assembled chunk instead.
/// </summary>
public static class LuaAiHostChecks
{
    [Check("scripting.lua-ai-host")]
    public static void HostRunsOneScriptInstancePerUnitAndDispatchesIntoIt()
    {
        var world = new WorldState();
        var engine = new FakeEngine();
        var failures = new List<AiScriptFailure>();
        var service = new LuaAiService(world, engine) { OnError = failures.Add };

        // ---- one instance per AI unit, created on first use with its OnInit ----

        var turret = Spawn(world, 1, EntityKind.Turret, UnitType.Turret, Team.Order);
        var hero = Spawn(world, 2, EntityKind.Hero, UnitType.Hero, Team.Chaos);
        TestAssert.True(service.Drives(turret), "every turret runs its script");
        TestAssert.False(service.Drives(hero), "nothing else is wired yet");

        var instance = service.InstanceFor(turret)!;
        TestAssert.True(failures.Count == 0,
            "the shipped Turret chunk and OnInit run without a failure: " +
            string.Join("; ", failures.Select(failure => failure.Hook + ": " + failure.Message)));
        TestAssert.Equal(1, service.Instances.Count(), "one instance per unit");
        TestAssert.Near(2.0, instance.State, 1e-9, "OnInit's SetState put the instance in AI_HARDIDLE");
        TestAssert.Equal(new[] { ("TimerFindEnemies", 0.15, true, (double?)null) }, instance.Timers.ToArray(),
            "OnInit armed one repeating timer and nothing has fired it yet");
        TestAssert.True(ReferenceEquals(instance, service.InstanceFor(turret)), "the second lookup returns the same instance");
        TestAssert.Equal(1, service.Instances.Count(), "the second lookup does not build another instance");

        var second = Spawn(world, 3, EntityKind.Turret, UnitType.Turret, Team.Order);
        var secondInstance = service.InstanceFor(second)!;
        TestAssert.Equal(2, service.Instances.Count(), "a second turret gets its own instance");
        TestAssert.False(ReferenceEquals(instance, secondInstance), "instances are per unit, not per script");
        TestAssert.Near(2.0, secondInstance.State, 1e-9, "each instance runs its own OnInit");

        // ---- the timer's cadence: lazy arm, then the phase is kept ----

        var enemy = Spawn(world, 4, EntityKind.Minion, UnitType.Minion, Team.Chaos);
        engine.Found = enemy;
        service.StepUnit(turret, 0.15, 0.15);
        TestAssert.Equal(1, engine.Count("FindTargetInAcR"), "the first deadline lands one interval after the step that armed it");
        service.StepUnit(turret, 0.29, 0.14);
        TestAssert.Equal(1, engine.Count("FindTargetInAcR"), "the repeating deadline stays on its .15 s cadence");
        // From here the scan is the ATTACKING one, which asks TargetInAttackRange instead of
        // searching: that call is what tells the two fires apart.
        service.StepUnit(turret, 0.45, 0.16);
        TestAssert.Equal(1, engine.Count("TargetInAttackRange"), "the phase survives a step that ran past the deadline");
        service.StepUnit(turret, 0.46, 0.01);
        TestAssert.Equal(1, engine.Count("TargetInAttackRange"), "at most one fire per step per timer");
        TestAssert.Near(30.0, instance.State, 1e-9, "the scan that found a target put the instance in AI_HARDIDLE_ATTACKING");
        TestAssert.Equal(enemy, engine.Current, "the scan's SetTarget is the instance's target");

        // ---- OnTargetLost is raised before the engine drops the id ----

        engine.StillValid = false;
        engine.Found = null;
        var findsBeforeLoss = engine.Count("FindTargetInAcR");
        service.StepUnit(turret, 0.50, 0.04);
        TestAssert.Equal(findsBeforeLoss + 1, engine.Count("FindTargetInAcR"), "the loss search runs from OnTargetLost, not from a timer");
        TestAssert.True(engine.Current is null, "an unreplaced loss is cleared by the engine after the callback ran");
        TestAssert.Near(2.0, instance.State, 1e-9, "the script returned the instance to AI_HARDIDLE");

        // The replacement the script keeps is the one that survives: the host only clears a loss
        // the script did not answer with its own SetTarget.
        var aggressor = Spawn(world, 5, EntityKind.Hero, UnitType.Hero, Team.Chaos);
        var other = Spawn(world, 6, EntityKind.Minion, UnitType.Minion, Team.Chaos);
        service.OnCallForHelp(turret, aggressor);
        TestAssert.Equal(aggressor, engine.Current, "OnCallForHelp's SetTarget is the instance's target");
        TestAssert.Near(30.0, instance.State, 1e-9, "OnCallForHelp's NetSetState is the instance's state");
        engine.StillValid = false;
        engine.Found = other;
        service.StepUnit(turret, 0.51, 0.01);
        TestAssert.Equal(other, engine.Current, "the script's own replacement survives the engine's clear");
        TestAssert.Near(30.0, instance.State, 1e-9, "the replacement keeps the instance in AI_HARDIDLE_ATTACKING");

        // ---- OnTauntBegin / OnTauntEnd take the taunter and hand it back ----

        engine.Taunt = aggressor;
        service.OnTauntBegin(turret);
        TestAssert.Equal(aggressor, engine.Current, "OnTauntBegin closes on the taunt target");
        TestAssert.Near(5.0, instance.State, 1e-9, "OnTauntBegin is the only path into AI_TAUNTED");
        engine.Taunt = null;
        engine.Found = enemy;
        service.OnTauntEnd(turret);
        TestAssert.Equal(enemy, engine.Current, "OnTauntEnd re-selects at once through its own TimerFindEnemies");
        TestAssert.Near(30.0, instance.State, 1e-9, "OnTauntEnd's own NetSetState is AI_HARDIDLE_ATTACKING");

        // ---- HaltAI: no timer, no swing, AI_HALTED, and nothing runs after ----

        service.HaltAI(turret);
        TestAssert.Equal(0, instance.Timers.Count, "HaltAI stopped the repeating timer");
        TestAssert.Near(0.0, instance.State, 1e-9, "HaltAI ends in AI_HALTED");
        engine.StillValid = true;
        var findCallsAfterHalt = engine.Count("FindTargetInAcR");
        service.StepUnit(turret, 10.0, 1.0);
        TestAssert.Equal(findCallsAfterHalt, engine.Count("FindTargetInAcR"), "a halted instance runs no timer");
        service.OnCallForHelp(turret, aggressor);
        TestAssert.Near(0.0, instance.State, 1e-9, "a halted instance ignores every event");

        // ---- timers the turret script never uses: one-shots and a reset ----

        var timerUnit = Spawn(world, 7, EntityKind.Turret, UnitType.Turret, Team.Chaos);
        var timerEngine = new FakeEngine();
        var timerFailures = new List<AiScriptFailure>();
        var timers = new LuaAiInstance(timerUnit, new LuaAiScript("Timers", TimersChunk()), timerEngine,
            new AiScriptCatalog(), ObjectFor, world.Get, timerFailures.Add);
        timers.Call("OnInit");
        TestAssert.Equal(new[]
        {
            ("Once", 1.0, false, (double?)null),
            ("Every", 0.5, true, (double?)null),
            ("Late", 0.5, false, (double?)null),
        }, timers.Timers.ToArray(), "InitTimer records the interval and the repeat flag");

        timers.Call("StopLate");                            // stops the one-shot before it can fire
        timerEngine.Now = 0.5;
        timers.Advance(0.5, 0.5);
        TestAssert.Equal(1, timerEngine.Count("FindTargetInAcR"), "only the repeating timer fired at .5");
        TestAssert.Equal(0, timerEngine.Count("GetTauntTarget"), "StopTimer keeps a stopped timer out of the pass");

        timerEngine.Now = 1.0;
        timers.Advance(1.0, 0.5);
        TestAssert.Equal(2, timerEngine.Count("FindTargetInAcR"), "the repeating timer kept its phase");
        TestAssert.Equal(1, timerEngine.Count("GetTarget"), "the one-shot fired once, at its full interval");
        TestAssert.Equal(new[] { ("Every", 0.5, true, (double?)1.5) }, timers.Timers.ToArray(),
            "a fired one-shot leaves the table");

        timers.Call("Restart");
        TestAssert.Equal(new[]
        {
            ("Once", 1.0, false, (double?)2.0),
            ("Every", 0.5, true, (double?)1.5),
        }, timers.Timers.ToArray(), "ResetAndStartTimer counts a full interval from the call (the clock is 1.0), not from the step");

        timerEngine.Now = 1.5;
        timers.Advance(1.5, 0.5);
        TestAssert.Equal(1, timerEngine.Count("GetTarget"), "the reset one-shot has not reached its new deadline");
        timerEngine.Now = 2.0;
        timers.Advance(2.0, 0.5);
        TestAssert.Equal(2, timerEngine.Count("GetTarget"), "the reset one-shot fires again, once");

        // ---- a script that raises is reported, and the instance keeps running ----

        TestAssert.Equal(0, timerFailures.Count, "the hand-assembled timers chunk runs without a failure: " +
            string.Join("; ", timerFailures.Select(failure => failure.Hook + ": " + failure.Message)));

        var boomUnit = Spawn(world, 8, EntityKind.Turret, UnitType.Turret, Team.Order);
        var boom = new LuaAiInstance(boomUnit, new LuaAiScript("Boom", BoomChunk()), timerEngine,
            new AiScriptCatalog(), ObjectFor, world.Get, timerFailures.Add);
        boom.Call("Boom");
        TestAssert.Equal(1, timerFailures.Count, "a raising callback is reported exactly once: " +
            string.Join("; ", timerFailures.Select(failure => failure.Hook + ": " + failure.Message)));
        TestAssert.Equal("Boom", timerFailures[0].Script, "the failure names the script");
        TestAssert.Equal("Boom", timerFailures[0].Hook, "the failure names the hook");
        TestAssert.True(timerFailures[0].Message.Length > 0, "the failure carries the interpreter's message");
        boom.Call("OnInit");
        TestAssert.Equal(1, timerFailures.Count, "the instance is still usable after a failure");
    }

    // ---- the two hand-assembled chunks ------------------------------------------------------------

    /// <summary>
    /// <c>function OnInit() InitTimer('Once', 1, false) InitTimer('Every', 0.5, true)
    /// InitTimer('Late', 0.5, false) end</c> plus one callback per timer and the two control
    /// hooks. Each timer callback asks a different native so a fire is attributable.
    /// </summary>
    private static LuaChunk TimersChunk()
    {
        var main = new LuaAsm { IsVararg = 2 };
        var init = new LuaAsm();
        foreach (var (name, interval, repeat) in new[]
                 {
                     ("Once", 1.0, false), ("Every", 0.5, true), ("Late", 0.5, false),
                 })
        {
            init.GetGlobal(0, init.Ks("InitTimer"));
            init.Load(1, name);
            init.Load(2, interval);
            init.LoadBool(3, repeat ? 1 : 0, 0);
            init.Call(0, 4, 1);
        }
        init.Return(0, 1);
        Define(main, init, "OnInit");
        Define(main, CallNative("GetTarget"), "Once");
        Define(main, CallNative("FindTargetInAcR"), "Every");
        Define(main, CallNative("GetTauntTarget"), "Late");
        Define(main, CallNative("ResetAndStartTimer", "Once"), "Restart");
        Define(main, CallNative("StopTimer", "Late"), "StopLate");
        main.Return(0, 1);                              // the main chunk only installs the closures
        return main.Chunk();
    }

    /// <summary><c>function Boom() local x = nil; return x.field end</c>: a genuine script error.</summary>
    private static LuaChunk BoomChunk()
    {
        var main = new LuaAsm { IsVararg = 2 };
        var body = new LuaAsm();
        body.LoadNil(0, 0);
        body.GetTable(1, 0, body.Ksk("field"));
        body.Return(0, 1);
        Define(main, body, "Boom");
        main.Return(0, 1);                              // the main chunk only installs the closure
        return main.Chunk();
    }

    /// <summary>The body of <c>function Name(a, b) Native(a, b) end</c>, with the arguments as constants.</summary>
    private static LuaAsm CallNative(string native, params string[] arguments)
    {
        var body = new LuaAsm { Params = (byte)arguments.Length };
        body.GetGlobal(0, body.Ks(native));
        for (var index = 0; index < arguments.Length; index++) body.Load(index + 1, arguments[index]);
        body.Call(0, arguments.Length + 1, 1);
        body.Return(0, 1);
        return body;
    }

    private static void Define(LuaAsm main, LuaAsm body, string name)
    {
        var prototype = main.AddChild(body);
        main.Closure(0, prototype);
        main.SetGlobal(0, main.Ks(name));
    }

    private static Entity Spawn(WorldState world, uint netId, EntityKind kind, UnitType type, Team team) =>
        world.Register(new Entity(netId, kind, type, team, new(7000, 7000))
        { Health = 1000, MaxHealth = 1000, AttackRange = 775 });

    private static LuaTable ObjectFor(Entity unit)
    {
        var table = new LuaTable();
        table.Set(LuaValue.Of("netId"), LuaValue.Of((double)unit.NetId));
        return table;
    }

    /// <summary>The world half of the natives: recorded, and answered from values the test sets.</summary>
    private sealed class FakeEngine : ILuaAiEngine
    {
        private readonly List<string> _calls = new();
        private double _state;

        public double Now { get; set; }
        public string LostTargetHook { get; set; } = "OnTargetLost";
        public Entity? Found { get; set; }
        public Entity? Current { get; set; }
        public Entity? Taunt { get; set; }
        public bool StillValid { get; set; } = true;
        public bool InRange { get; set; } = true;
        public IReadOnlyList<string> Calls => _calls;

        public int Count(string name) =>
            _calls.Count(call => call.StartsWith(name + "(", StringComparison.Ordinal));

        public Entity? FindTargetInAcquisitionRange(Entity unit) => Note("FindTargetInAcR", Found);

        public void SetTarget(Entity unit, Entity? target) => Current = Note("SetTarget", target);

        public Entity? CurrentTarget(Entity unit) => Note("GetTarget", Current);

        public uint? HeldTargetId(Entity unit) => Current?.NetId;

        public Entity? TauntTarget(Entity unit) => Note("GetTauntTarget", Taunt);

        public bool TargetStillValid(Entity unit, Entity target) => Note("TargetStillValid", StillValid);

        public bool TargetInAttackRange(Entity unit, Entity? target) => Note("TargetInAttackRange", InRange);

        public void TurnOnAutoAttack(Entity unit, Entity? target) => Note("TurnOnAutoAttack", target);

        public void TurnOffAutoAttack(Entity unit, double stopReason) => Note("TurnOffAutoAttack", stopReason);

        public double AiState(Entity unit) => _state;

        public void SetAiState(Entity unit, double state) => Note("SetAiState", _state = state);

        private T Note<T>(string name, T answer)
        {
            var shown = answer is Entity entity ? entity.NetId.ToString()
                : answer is null ? "nil"
                : answer.ToString() ?? "nil";
            _calls.Add(name + "(" + shown + ")");
            return answer;
        }
    }
}
