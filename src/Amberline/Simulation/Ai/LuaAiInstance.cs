using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.Lua;
using LuaChunk = Amberline.Data.LuaChunk;
using LuaValue = Amberline.Scripting.Lua.LuaValue;

namespace Amberline.Simulation.Ai;

/// <summary>One script failure, reported the way the BuildingBlocks scripts already are.</summary>
public sealed record AiScriptFailure(string Script, string Hook, string Message);

/// <summary>
/// One AI unit's script instance: its own <see cref="LuaState"/> (so its globals and its timers
/// are its own), its AI state id, and the host natives. Retail ran one instance per AI unit and
/// dispatched into its callbacks; this is that, over the batch-139 Lua 5.1 interpreter.
/// </summary>
public sealed class LuaAiInstance
{
    private readonly Entity _unit;
    private readonly ILuaAiEngine _engine;
    private readonly AiScriptCatalog _catalog;
    private readonly Func<Entity, LuaTable> _objectFor;
    private readonly Func<uint, Entity?> _worldGet;
    private readonly Action<AiScriptFailure>? _onError;
    private readonly LuaState _state;
    private readonly List<AiTimer> _timers = new();
    private bool _timerFiredThisStep;

    public LuaAiInstance(Entity unit, LuaAiScript script, ILuaAiEngine engine, AiScriptCatalog catalog,
        Func<Entity, LuaTable> objectFor, Func<uint, Entity?> worldGet, Action<AiScriptFailure>? onError, CrtState? random = null)
    {
        _state = new LuaState(random);
        _unit = unit;
        _engine = engine;
        _catalog = catalog;
        _objectFor = objectFor;
        _worldGet = worldGet;
        _onError = onError;
        Script = script;
        _state.OpenLibs();
        AiGlobals.Install(_state);
        _state.SetGlobal("me", LuaValue.Of(objectFor(unit)));
        InstallOsLibrary();
        RegisterNatives();
        // The main chunk only closes its callbacks over the globals; running it is what defines
        // OnInit, OnTargetLost, TimerFindEnemies and the rest. OnInit is the engine's to call.
        Body("main chunk", () => _state.DoChunk(script.Chunk));
    }

    public LuaAiScript Script { get; }

    public Entity Unit => _unit;

    /// <summary>
    /// The current <c>AI_*</c> id. The engine owns the store: for a jungle unit it is the same
    /// field an external <see cref="Entity.NeutralState"/> write is adopted into, so
    /// <c>GetState</c> answers the state the rest of the match observes on this step.
    /// </summary>
    public double State
    {
        get => _engine.AiState(_unit);
        set => _engine.SetAiState(_unit, value);
    }

    /// <summary>
    /// Whether one of the script's timers ran in the last <see cref="Advance"/>: the steps the shipped AI schedules its decisions on
    /// (Pet.lua's 0.15 s scans and the like). The engines issue their orders on those steps and nowhere else (batch 229 follow-up).
    /// </summary>
    public bool TimerFiredThisStep => _timerFiredThisStep;

    /// <summary>The live timers, for the checks.</summary>
    public IReadOnlyList<(string Name, double Interval, bool Repeat, double? Deadline)> Timers =>
        _timers.Where(timer => !timer.Stopped)
            .Select(timer => (timer.Name, timer.Interval, timer.Repeat, timer.Deadline))
            .ToArray();

    /// <summary>
    /// Dispatches one engine event into the script's callback of the same name and answers its
    /// first return value — <c>OnOrder</c>'s accept flag, the one return the corpus has. A callback
    /// that is missing or raises answers nil.
    /// </summary>
    public LuaValue Call(string hook, params LuaValue[] arguments) =>
        Run(hook, () => _state.CallGlobal(hook, arguments));

    /// <summary>
    /// One of the script's own globals, read back after a step. Bot.luaobj keeps its decision in
    /// <c>tasks</c> and <c>activeTask</c>, so this is how the engine observes what the script
    /// decided rather than re-deriving it. A name the script never set reads as nil.
    /// </summary>
    public LuaValue GetGlobal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _state.GetGlobal(name);
    }

    /// <summary>
    /// Overwrites one of the script's own globals after the chunk has run. The scripts keep their
    /// tuning in these (<c>MAX_ENGAGE_DISTANCE</c>, <c>DELAY_FIND_ENEMIES</c>,
    /// <c>FEAR_WANDER_DISTANCE</c>) and read it at use, so a red control that needs a different
    /// value writes the global rather than asking the engine for one the script never consults.
    /// </summary>
    public void SetGlobal(string name, double value)
    {
        ArgumentNullException.ThrowIfNull(name);
        _state.SetGlobal(name, LuaValue.Of(value));
    }

    /// <summary>
    /// Makes every live timer run on the next step and keep its phase from that step: the re-base the
    /// old hand-written machines answered when a check zeroed a pet's next scan
    /// (<c>TibbersAiSystem.ScanNow</c>, <c>SharedCloneService</c>'s <c>NextAi = 0</c>). The checks
    /// that walk a script's 0.15 s cadence on a time axis of their own re-base the phase with it,
    /// since the host's own steps have already advanced the timers past that axis.
    /// </summary>
    public void RephaseTimers()
    {
        foreach (var timer in _timers)
            if (!timer.Stopped) timer.ForceNext = true;
    }

    /// <summary>The per-tick pass: every timer whose interval has run out, at most once per step each.</summary>
    public void Advance(double now, double dt)
    {
        _timerFiredThisStep = false;
        // First the clocks: a timer ResetAndStartTimer started counts its own interval, and this
        // step's dt is what it runs. A lazy InitTimer arm has no counter and stays on the
        // Deadline grid below.
        foreach (var timer in _timers.ToArray())
        {
            if (!timer.Stopped && timer.Elapsed is { } counted) timer.Elapsed = counted + dt;
        }
        foreach (var timer in _timers.ToArray())
        {
            if (timer.Stopped) continue;
            // A timer registered this match first fires one interval after the start of the step
            // the host observed it in. This is TurretScanReady's lazy arm, which the turret scan
            // checks pin; InitTimer itself only records the interval and the repeat flag.
            timer.Deadline ??= now - dt + timer.Interval;
            var forceNext = timer.ForceNext;
            timer.ForceNext = false;
            if (forceNext)
            {
                timer.Deadline = now - dt;
                // RephaseTimers: this step is the fire step, and the counter re-bases from its start.
                if (timer.Elapsed is not null) timer.Elapsed = timer.Interval;
            }
            // Two gates, because the two arming verbs measure their interval against two different
            // clocks. ResetAndStartTimer's is the timer's own counter — the one neutral-ai-corpus
            // records as neutral_regen_elapsed — and ten accumulated 0.1 s steps are
            // 0.9999999999999999, a hair under a whole second, so the tenth step does not fire a
            // 1.0 s timer: chase arms TimerRegen on frame 69 and the reference publishes on 80,
            // then every ten. The lazy InitTimer arm keeps the host-clock deadline the scan checks
            // pin (minions.retail-target-lifecycle counts a 0.25 s grid's last boundary against a
            // clock that is 4.44e-15 under 4.0 s, and that boundary has to land).
            if (!forceNext && (timer.Elapsed is { } counted
                    ? counted < timer.Interval
                    : now + 1e-9 < timer.Deadline)) continue;
            _timerFiredThisStep = true;
            Run(timer.Name, () => _state.CallGlobal(timer.Name));
            if (!timer.Repeat)
            {
                timer.Stopped = true;
                continue;
            }
            if (timer.Interval <= 0.0)
            {
                // A zero-interval repeat is every step (Leashed's TimerAttack): no phase to keep,
                // and the interval cannot divide the overrun.
                if (timer.Elapsed is not null) timer.Elapsed = 0.0;
                timer.Deadline = now;
                continue;
            }
            // One fire per step, and whatever this step ran past a whole interval counts toward
            // the next one; the Deadline the checks' Timers rows read moves on with it.
            if (timer.Elapsed is { } overrun) timer.Elapsed = overrun % timer.Interval;
            var overdue = Math.Max(0.0, now - timer.Deadline.Value);
            timer.Deadline += (Math.Floor(overdue / timer.Interval) + 1.0) * timer.Interval;
        }
    }

    /// <summary>Runs the callback the script defined under this name, when there is one.</summary>
    private LuaValue Run(string hook, Func<LuaValue[]> body)
    {
        if (!_state.TryGetGlobal(hook, out var entry) || entry.Type != LuaType.Function)
            return LuaValue.Nil;
        return Body(hook, body);
    }

    /// <summary>Runs a piece of the script and reports what it raises, named after the hook.</summary>
    private LuaValue Body(string hook, Func<LuaValue[]> body)
    {
        try
        {
            var results = body();
            return results.Length > 0 ? results[0] : LuaValue.Nil;
        }
        catch (Exception error)
        {
            _onError?.Invoke(new AiScriptFailure(Script.Name, hook, error.Message));
            return LuaValue.Nil;
        }
    }

    /// <summary>
    /// The host clock the scripts read through <c>os.time()</c>. Retail's is the wall clock; the
    /// match clock floored to whole seconds keeps a run reproducible and gives the granularity
    /// TaskRetreat's health history needs, which uses the answer as a table key and looks two
    /// seconds back (<c>Reconstruction:</c> — nothing in the shipped data says which clock retail
    /// read, and a fractional one would make that history never find its own entry).
    /// </summary>
    private void InstallOsLibrary()
    {
        var os = new LuaTable();
        os.Set(LuaValue.Of("time"), LuaValue.Of(new LuaNative("os.time",
            (_, _) => new[] { LuaValue.Of(Math.Floor(_engine.Now)) })));
        _state.SetGlobal("os", LuaValue.Of(os));
    }

    /// <summary>
    /// A task module's environment: its own globals first, the host's underneath. The shipped
    /// modules are written as if they had the whole global table to themselves (each SETGLOBALs
    /// its own <c>UpdatePriority</c>/<c>BeginTask</c>/<c>Tick</c> and its own AI state ids), so
    /// <c>LoadScript</c> hands each one a fresh table; the <c>__index</c> fallback is what lets a
    /// module's GETGLOBAL of a native, of <c>math</c> or of the AI id table resolve at all.
    /// </summary>
    private LuaTable ModuleEnvironment()
    {
        var environment = new LuaTable();
        var metatable = new LuaTable();
        metatable.Set(LuaValue.Of("__index"), LuaValue.Of(_state.Globals));
        environment.Metatable = metatable;
        return environment;
    }

    /// <summary>
    /// The unit's own script folder under <c>DATA</c>, the first place a <c>LoadScript</c> name
    /// is looked for. A hero's is <c>Characters/&lt;Champion&gt;/Scripts</c>, where a champion's
    /// own <c>TaskCastSpell.luaobj</c> lives; a unit with no champion name gets no folder and the
    /// shared modules are used as they are.
    /// </summary>
    private string? ChampionFolder() =>
        _unit.UnitType == UnitType.Hero && _unit.ChampionName.Length > 0
            ? Path.Combine("Characters", _unit.ChampionName, "Scripts")
            : null;

    private void RegisterNatives()
    {
        _state.Register("GetState", (_, _) => new[] { LuaValue.Of(State) });

        // The geometry pair and the clock the client registers alongside InitTimer (the
        // level-script table carries GetTime with it; the bot tasks name GetDist and
        // GetDistSquared on the same points). Both arguments are either a point a native handed
        // out or a unit's object, and the answer is the flat map distance between them.
        _state.Register("GetTime", (_, _) => new[] { LuaValue.Of(_engine.Now) });

        _state.Register("GetDist", (_, args) => new[]
        {
            LuaValue.Of(Distance(PositionOf(args, 0), PositionOf(args, 1))),
        });

        _state.Register("GetDistSquared", (_, args) => new[]
        {
            LuaValue.Of(DistanceSquared(PositionOf(args, 0), PositionOf(args, 1))),
        });

        _state.Register("SetState", (_, args) =>
        {
            // The engine is told about every state change, not only the netted one: the jungle
            // natives project Entity.NeutralState off it and Leashed calls SetState where a turret
            // would call NetSetState (OnStoppedMoving ends its retreat that way).
            State = Number(args, 0);
            return Array.Empty<LuaValue>();
        });

        _state.Register("NetSetState", (_, args) =>
        {
            State = Number(args, 0);
            return Array.Empty<LuaValue>();
        });

        _state.Register("FindTargetInAcR", (_, _) =>
            new[] { ObjectOf(_engine.FindTargetInAcquisitionRange(_unit)) });

        _state.Register("SetTarget", (_, args) =>
        {
            _engine.SetTarget(_unit, UnitOf(args, 0));
            return Array.Empty<LuaValue>();
        });

        _state.Register("GetTarget", (_, _) => new[] { ObjectOf(_engine.CurrentTarget(_unit)) });

        _state.Register("GetTauntTarget", (_, _) => new[] { ObjectOf(_engine.TauntTarget(_unit)) });

        _state.Register("TargetInAttackRange", (_, args) => new[]
        {
            LuaValue.Of(_engine.TargetInAttackRange(_unit,
                args.Length > 0 ? UnitOf(args, 0) : _engine.CurrentTarget(_unit))),
        });

        _state.Register("TurnOnAutoAttack", (_, args) =>
        {
            _engine.TurnOnAutoAttack(_unit,
                args.Length > 0 ? UnitOf(args, 0) : _engine.CurrentTarget(_unit));
            return Array.Empty<LuaValue>();
        });

        _state.Register("TurnOffAutoAttack", (_, args) =>
        {
            _engine.TurnOffAutoAttack(_unit, Number(args, 0));
            return Array.Empty<LuaValue>();
        });

        _state.Register("InitTimer", (_, args) =>
        {
            var name = Text(args, 0);
            if (name.Length == 0) return Array.Empty<LuaValue>();
            // The new arm replaces the old timer outright. Advance walks a snapshot of the list, so a replaced
            // timer still due later in the same step would fire on its old deadline (batch 581: TaskKillHero's
            // BeginTask re-arms AntiKiteTimer from inside TimerUpdate, proto 0.1 0010-0014, and the stale one then
            // compared the TargetHealth BeginTask had just read, which latches ReducePriority for good).
            foreach (var timer in _timers)
                if (timer.Name == name) timer.Stopped = true;
            _timers.RemoveAll(timer => timer.Name == name);
            _timers.Add(new AiTimer(name, Math.Max(0.0, Number(args, 1)), args.Length > 2 && args[2].Truthy));
            return Array.Empty<LuaValue>();
        });

        _state.Register("StopTimer", (_, args) =>
        {
            foreach (var timer in _timers.Where(timer => timer.Name == Text(args, 0)))
                timer.Stopped = true;
            return Array.Empty<LuaValue>();
        });

        _state.Register("ResetAndStartTimer", (_, args) =>
        {
            foreach (var timer in _timers.Where(timer => timer.Name == Text(args, 0)))
            {
                timer.Stopped = false;
                // A reset counts from the call, not from the step's start: the one-shots it
                // re-arms (TimerAntiKite, TimerFeared, TimerRegen) want a fresh full interval.
                // The counter starts at zero on the call's own frame, which is what makes a reset
                // spend no dt of the frame it lands on (chase's regen reads 0 on frame 69 and 0.1
                // on 70).
                timer.Elapsed = 0.0;
                timer.Deadline = _engine.Now + timer.Interval;
            }
            return Array.Empty<LuaValue>();
        });

        // The loaders the AI chains use: minionLib is dofile'd into the caller's own environment.
        // Not used by Turret; they are here so the next script can run without a host change.
        _state.Register("dofile", (_, args) =>
        {
            _catalog.RunIn(_state, Text(args, 0));
            return Array.Empty<LuaValue>();
        });
        _state.Register("LoadScriptIntoScript", (_, args) =>
        {
            _catalog.RunIn(_state, Text(args, 0));
            return Array.Empty<LuaValue>();
        });
        _state.Register("LoadScript", (_, args) =>
        {
            // The bot task module: the named chunk's own environment table. Each module keeps
            // its own globals — TaskKillMinion's AI_ATTACK is 0 where TaskDefendStructure's is 1
            // — so the environment is fresh per call, and it falls back to the host's globals so
            // the natives, `math`, `os` and the AI id table still resolve for it. The unit's own
            // Characters/<Name>/Scripts folder is searched first: a champion that ships its own
            // TaskCastSpell.luaobj runs that one, and the shared module is what every other
            // LoadTask name falls through to.
            var name = Text(args, 0);
            var environment = ModuleEnvironment();
            var chunk = _catalog.Load(name, ChampionFolder());
            _state.Call(LuaValue.Of(new LuaClosure(chunk.TopLevel, Array.Empty<LuaUpvalue>(), environment)));
            // A bot's per-champion module data is reconstructed here when the shipped files do not
            // carry it (see ILuaAiBotEngine.ReconstructModuleData); every other engine family has
            // nothing missing and so never enters.
            if (_engine is ILuaAiBotEngine bot) bot.ReconstructModuleData(_unit, name, environment);
            return new[] { LuaValue.Of(environment) };
        });

        // The ownership half: the natives a jungle monster and a pet both ask for. They are absent
        // for a turret (Turret.luaobj never names one) so a missing native is a real error.
        if (_engine is ILuaAiOwnedEngine owned)
        {
            // The state-and-* natives set the AI state themselves, in the same step as the target
            // or the path they name. Writing State first would run SetAiState's attack-state
            // re-path on the target being replaced — two path requests in one native call.
            _state.Register("SetStateAndCloseToTarget", (_, args) =>
            {
                owned.SetStateAndCloseToTarget(_unit, Number(args, 0), UnitOf(args, 1));
                return Array.Empty<LuaValue>();
            });

            _state.Register("SetStateAndMove", (_, args) =>
            {
                owned.SetStateAndMove(_unit, Number(args, 0), PointOf(args, 1));
                return Array.Empty<LuaValue>();
            });

            _state.Register("MakeWanderPoint", (_, args) => new[]
            {
                PointValue(owned.MakeWanderPoint(_unit, PointOf(args, 0), Number(args, 1))),
            });

            _state.Register("GetFearLeashPoint", (_, _) => new[] { PointValue(owned.GetFearLeashPoint(_unit)) });

            // The pet scripts call GetOwner() / GetGoldRedirectTarget() with no argument (their own
            // owner); Leashed passes the object whose owner it reads. Both are the same native.
            _state.Register("GetOwner", (_, args) => new[]
            {
                ObjectOf(owned.GetOwner(args.Length > 0 ? NetIdOf(args, 0) : _unit.NetId)),
            });

            _state.Register("GetGoldRedirectTarget", (_, args) => new[]
            {
                ObjectOf(owned.GetGoldRedirectTarget(args.Length > 0 ? NetIdOf(args, 0) : _unit.NetId)),
            });

            _state.Register("TargetInCancelAttackRange", (_, args) => new[]
            {
                LuaValue.Of(owned.TargetInCancelAttackRange(_unit,
                    args.Length > 0 ? UnitOf(args, 0) : _engine.CurrentTarget(_unit))),
            });
        }

        // The pet natives, bound only for a unit whose engine carries the pet half. A jungle engine
        // carries none of them (Leashed names none), so this is a no-op there.
        RegisterPetNatives();

        // The lane-minion natives, bound only for a unit whose engine carries the minion half.
        RegisterMinionNatives();

        // The champion natives, bound only for a unit whose engine carries the hero half.
        RegisterHeroNatives();

        // The bot natives, bound only for a unit whose engine carries the bot half.
        RegisterBotNatives();

        // The jungle natives, bound only for a unit whose engine carries the jungle half.
        if (_engine is not ILuaAiLeashedEngine jungle) return;

        _state.Register("SetStateAndMoveToLeashedPos", (_, args) =>
        {
            jungle.SetStateAndMoveToLeashedPos(_unit, Number(args, 0));
            return Array.Empty<LuaValue>();
        });

        _state.Register("SetMyLeashedPos", (_, _) =>
        {
            jungle.SetMyLeashedPos(_unit);
            return Array.Empty<LuaValue>();
        });

        _state.Register("GetMyLeashedPos", (_, _) => new[] { PointValue(jungle.GetMyLeashedPos(_unit)) });

        _state.Register("GetDistToLeashedPos", (_, _) => new[] { LuaValue.Of(jungle.GetDistToLeashedPos(_unit)) });

        _state.Register("GetDistToRetreat", (_, _) => new[] { LuaValue.Of(jungle.GetDistToRetreat(_unit)) });

        _state.Register("IsMovementStopped", (_, _) => new[] { LuaValue.Of(jungle.IsMovementStopped(_unit)) });

        _state.Register("GetRoamState", (_, _) => new[] { LuaValue.Of(jungle.GetRoamState(_unit)) });

        _state.Register("SetRoamState", (_, args) =>
        {
            jungle.SetRoamState(_unit, Number(args, 0));
            return Array.Empty<LuaValue>();
        });

        _state.Register("GetOriginalState", (_, _) => new[] { LuaValue.Of(jungle.GetOriginalState(_unit)) });

        _state.Register("GetLeashCounter", (_, _) => new[] { LuaValue.Of(jungle.GetLeashCounter(_unit)) });

        _state.Register("SetLeashCounter", (_, args) =>
        {
            jungle.SetLeashCounter(_unit, Number(args, 0));
            return Array.Empty<LuaValue>();
        });

        _state.Register("AddValidTarget", (_, args) =>
        {
            jungle.AddValidTarget(_unit, UnitOf(args, 0));
            return Array.Empty<LuaValue>();
        });

        _state.Register("ClearValidTargets", (_, _) =>
        {
            jungle.ClearValidTargets(_unit);
            return Array.Empty<LuaValue>();
        });

        _state.Register("FindValidTargetNearPosition", (_, args) => new[]
        {
            ObjectOf(jungle.FindValidTargetNearPosition(_unit, PointOf(args, 0), Number(args, 1))),
        });

        _state.Register("GetHP", (_, _) => new[] { LuaValue.Of(jungle.GetHP(_unit)) });

        _state.Register("GetMaxHP", (_, _) => new[] { LuaValue.Of(jungle.GetMaxHP(_unit)) });

        _state.Register("SetHP", (_, args) =>
        {
            jungle.SetHP(_unit, Number(args, 0));
            return Array.Empty<LuaValue>();
        });

        _state.Register("DistanceBetweenObjectAndPoint", (_, args) => new[]
        {
            LuaValue.Of(jungle.DistanceBetweenObjectAndPoint(UnitOf(args, 0), PointOf(args, 1))),
        });

        _state.Register("Error", (_, args) =>
        {
            jungle.Error(_unit, Text(args, 0));
            return Array.Empty<LuaValue>();
        });
    }

    /// <summary>
    /// The pet natives, bound only for a unit whose engine carries the pet half. Pet.luaobj,
    /// UncontrollablePet.luaobj and YorickPHPet.luaobj name every one of them; a turret names none,
    /// so a missing native is a real error rather than a silent no-op.
    /// </summary>
    private void RegisterPetNatives()
    {
        if (_engine is not ILuaAiPetEngine pet) return;

        _state.Register("SetStateAndMoveInPos", (_, args) =>
        {
            pet.SetStateAndMoveInPos(_unit, Number(args, 0));
            return Array.Empty<LuaValue>();
        });

        _state.Register("SetLastPosPointWithObj", (_, args) =>
        {
            pet.SetLastPosPointWithObj(_unit, UnitOf(args, 0));
            return Array.Empty<LuaValue>();
        });

        _state.Register("DistanceBetweenObjectAndInPosSq", (_, args) => new[]
        {
            LuaValue.Of(pet.DistanceBetweenObjectAndInPosSq(UnitOf(args, 0))),
        });

        _state.Register("DistanceBetweenObjects", (_, args) => new[]
        {
            LuaValue.Of(pet.DistanceBetweenObjects(UnitOf(args, 0), UnitOf(args, 1))),
        });

        _state.Register("GetPetReturnRadius", (_, args) => new[]
        {
            LuaValue.Of(pet.GetPetReturnRadius(args.Length > 0 ? UnitOf(args, 0) : _unit)),
        });

        _state.Register("GetCanMove", (_, args) => new[]
        {
            LuaValue.Of(pet.GetCanMove(args.Length > 0 ? UnitOf(args, 0) ?? _unit : _unit)),
        });

        _state.Register("IsMoving", (_, _) => new[] { LuaValue.Of(pet.IsMoving(_unit)) });

        _state.Register("IsNetworkLocal", (_, _) => new[] { LuaValue.Of(pet.IsNetworkLocal(_unit)) });

        _state.Register("OwnerIsMoving", (_, _) => new[] { LuaValue.Of(pet.OwnerIsMoving(_unit)) });

        _state.Register("SetActorPositionFromObject", (_, args) =>
        {
            pet.SetActorPositionFromObject(args.Length > 0 ? UnitOf(args, 0) ?? _unit : _unit,
                UnitOf(args, 1));
            return Array.Empty<LuaValue>();
        });

        _state.Register("FindTargetInAcRUsingGoldRedirectTarget", (_, _) => new[]
        {
            ObjectOf(pet.FindTargetInAcquisitionRangeOfGoldRedirectTarget(_unit)),
        });

        _state.Register("Die", (_, args) =>
        {
            pet.Die(args.Length > 0 ? UnitOf(args, 0) ?? _unit : _unit, Number(args, 1));
            return Array.Empty<LuaValue>();
        });

        _state.Register("Say", (_, args) =>
        {
            pet.Say(_unit, Text(args, 0));
            return Array.Empty<LuaValue>();
        });
    }

    /// <summary>
    /// The natives only a lane minion names, bound only for a unit whose engine carries the minion
    /// half. Minion.luaobj and minionLib.luaobj name these five and no other family does
    /// (<see cref="ILuaAiMinionEngine"/>); a turret, a camp monster or a pet names none of them, so
    /// a missing native is a real error rather than a silent no-op.
    /// </summary>
    private void RegisterMinionNatives()
    {
        if (_engine is not ILuaAiMinionEngine minion) return;

        _state.Register("SetStateAndMoveToForwardNav", (_, args) =>
        {
            minion.SetStateAndMoveToForwardNav(_unit, Number(args, 0));
            return Array.Empty<LuaValue>();
        });

        _state.Register("LastAutoAttackFinished", (_, _) => new[]
        {
            LuaValue.Of(minion.LastAutoAttackFinished(_unit)),
        });

        _state.Register("AddToIgnore", (_, args) =>
        {
            minion.AddToIgnore(_unit, Number(args, 0));
            return Array.Empty<LuaValue>();
        });

        _state.Register("DistanceBetweenMeAndObject", (_, args) => new[]
        {
            LuaValue.Of(minion.DistanceBetweenMeAndObject(_unit, UnitOf(args, 0))),
        });

        _state.Register("IsMoving", (_, _) => new[] { LuaValue.Of(minion.IsMoving(_unit)) });

        // Batch 451: the four natives of MinionOdin.luaobj and MinionReverse.luaobj (ILuaAiMinionEngine).
        _state.Register("FindUseableObjectInAcR", (_, _) => new[] { ObjectOf(minion.FindUseableObjectInAcR(_unit)) });
        _state.Register("IsObjectUseable", (_, args) => new[] { LuaValue.Of(minion.IsObjectUseable(_unit, UnitOf(args, 0))) });
        _state.Register("UseTarget", (_, args) =>
        {
            minion.UseTarget(_unit, UnitOf(args, 0));
            return Array.Empty<LuaValue>();
        });
        _state.Register("SetStateAndMoveToBackwardNav", (_, args) =>
        {
            minion.SetStateAndMoveToBackwardNav(_unit, Number(args, 0));
            return Array.Empty<LuaValue>();
        });
    }

    /// <summary>
    /// The natives only a champion names, bound only for a unit whose engine carries the hero half
    /// (<see cref="ILuaAiHeroEngine"/>). Hero.luaobj names these fourteen and no other family does,
    /// so a missing native is a real error rather than a silent no-op. The movement, combat and cast
    /// plumbing behind them stays the engine's; the chunk's callbacks and its 0.2 s timers are the
    /// only rule set here.
    /// </summary>
    private void RegisterHeroNatives()
    {
        if (_engine is not ILuaAiHeroEngine hero) return;

        _state.Register("SetStateAndMoveInPos", (_, args) =>
        {
            hero.SetStateAndMoveInPos(_unit, Number(args, 0));
            return Array.Empty<LuaValue>();
        });

        // The two take no argument: the point is the one the caller staged before OnOrder (the
        // contract LuaAiService.Order states), which is the click itself.
        _state.Register("AssignTargetPosInPos", (_, _) =>
        {
            hero.AssignTargetPosInPos(_unit);
            return Array.Empty<LuaValue>();
        });

        _state.Register("ClearTargetPosInPos", (_, _) =>
        {
            hero.ClearTargetPosInPos(_unit);
            return Array.Empty<LuaValue>();
        });

        _state.Register("DistanceBetweenObjectAndTargetPosSq", (_, args) => new[]
        {
            LuaValue.Of(hero.DistanceBetweenObjectAndTargetPosSq(_unit, UnitOf(args, 0))),
        });

        _state.Register("GetTargetOrFindTargetInAcR", (_, _) => new[]
        {
            ObjectOf(hero.GetTargetOrFindTargetInAcquisitionRange(_unit)),
        });

        _state.Register("GetLostTargetIfVisible", (_, _) => new[] { ObjectOf(hero.GetLostTargetIfVisible(_unit)) });

        _state.Register("CanSeeMe", (_, args) => new[] { LuaValue.Of(hero.CanSeeMe(_unit, UnitOf(args, 0))) });

        _state.Register("IsTargetLost", (_, _) => new[] { LuaValue.Of(hero.IsTargetLost(_unit)) });

        _state.Register("IsMovementStopped", (_, _) => new[] { LuaValue.Of(hero.IsMovementStopped(_unit)) });

        _state.Register("IsMoving", (_, _) => new[] { LuaValue.Of(hero.IsMoving(_unit)) });

        _state.Register("LastAutoAttackFinished", (_, _) => new[] { LuaValue.Of(hero.LastAutoAttackFinished(_unit)) });

        _state.Register("GetPercentAttackSpeedMod", (_, _) => new[] { LuaValue.Of(hero.GetPercentAttackSpeedMod(_unit)) });

        _state.Register("SpellBuffRemoveType", (_, args) =>
        {
            hero.SpellBuffRemoveType(_unit, UnitOf(args, 0), Number(args, 1));
            return Array.Empty<LuaValue>();
        });

        _state.Register("Say", (_, args) =>
        {
            hero.Say(_unit, Text(args, 0));
            return Array.Empty<LuaValue>();
        });
    }

    /// <summary>
    /// The natives of the bot family, bound only for a unit whose engine carries that half
    /// (<see cref="ILuaAiBotEngine"/>). <c>Bot.luaobj</c> and the eight <c>Task*.luaobj</c> modules
    /// it loads name these; a turret, a camp monster, a lane minion or a pet names none of them,
    /// so a missing native is a real error rather than a silent no-op. The shared set above
    /// (states, target, auto-attack, timers, the loaders) is already bound for every engine.
    /// </summary>
    private void RegisterBotNatives()
    {
        if (_engine is not ILuaAiBotEngine bot) return;

        // Reconstruction (batch 581): AI_SOFTATTACK reads 1 for the bot family. It is the one host AI state id the bot
        // scripts read (TaskPushLane proto 0.2 0005: SetStateAndMove(AI_SOFTATTACK, ...)); every task module numbers its own
        // states 0 and 1 (TaskKillMinion/TaskKillTower AI_ATTACK 0, AI_FOLLOW 1; TaskKillHero, TaskBuyItem,
        // TaskDefendStructure likewise). TaskKillTower's BeginTask sets no state (proto 0.2) and its Tick acts only on AI_FOLLOW
        // or AI_ATTACK (proto 0.4 0014-0032), so with the hero table's 8 a bot that came to a tower behind its Push Lane
        // stood beside it doing nothing (half of every tower task in seed 9801). Retail's number is unrecovered; 1 is the
        // AI_FOLLOW both kill modules name, which the push task's follow is. No bot script reads AI_IDLE, which shares it.
        _state.SetGlobal("AI_SOFTATTACK", LuaValue.Of(1.0));

        // ---- orders ----

        _state.Register("SetStateAndCloseToTarget", (_, args) =>
        {
            bot.SetStateAndCloseToTarget(_unit, Number(args, 0), UnitOf(args, 1));
            return Array.Empty<LuaValue>();
        });

        _state.Register("SetStateAndMove", (_, args) =>
        {
            bot.SetStateAndMove(_unit, Number(args, 0), PositionOf(args, 1));
            return Array.Empty<LuaValue>();
        });

        _state.Register("StopMove", (_, _) =>
        {
            bot.StopMove(_unit);
            return Array.Empty<LuaValue>();
        });

        _state.Register("IsMoving", (_, _) => new[] { LuaValue.Of(bot.IsMoving(_unit)) });

        _state.Register("BuyItem", (_, args) =>
        {
            bot.BuyItem(_unit, (int)Number(args, 0));
            return Array.Empty<LuaValue>();
        });

        _state.Register("ServerCastSpell", (_, args) => new[]
        {
            LuaValue.Of(bot.ServerCastSpell(_unit, Number(args, 0))),
        });

        _state.Register("UpgradeSpellsInOrder", (_, args) =>
        {
            bot.UpgradeSpellsInOrder(_unit, args.Select(argument => argument.ToNumber() ?? 0.0).ToArray());
            return Array.Empty<LuaValue>();
        });

        _state.Register("SetDebugSlotText", (_, args) =>
        {
            bot.SetDebugSlotText(_unit, Number(args, 0), Text(args, 1), Number(args, 2));
            return Array.Empty<LuaValue>();
        });

        _state.Register("Say", (_, args) =>
        {
            bot.Say(_unit, Text(args, 0));
            return Array.Empty<LuaValue>();
        });

        // ---- the unit ----

        _state.Register("GetAttackRange", (_, _) => new[] { LuaValue.Of(bot.AttackRange(_unit)) });

        _state.Register("GetMP", (_, _) => new[] { LuaValue.Of(bot.Mana(_unit)) });

        _state.Register("GetMaxMP", (_, _) => new[] { LuaValue.Of(bot.MaxMana(_unit)) });

        _state.Register("GetGold", (_, _) => new[] { LuaValue.Of(bot.Gold(_unit)) });

        _state.Register("GetNumInvSlotsAvailable", (_, _) =>
            new[] { LuaValue.Of(bot.FreeInventorySlots(_unit)) });

        _state.Register("GetItemPrice", (_, args) => new[]
        {
            LuaValue.Of(bot.ItemPrice(_unit, (int)Number(args, 0))),
        });

        _state.Register("CanUseSpell", (_, args) => new[]
        {
            LuaValue.Of(bot.CanUseSpell(_unit, Number(args, 0))),
        });

        // ---- the world ----

        // GetPos() is the hero's own position, the one form without an argument; GetPos(object)
        // is that unit's, and a unit that has already left the world reads as the map origin like
        // every other distance argument.
        _state.Register("GetPos", (_, args) => new[]
        {
            PointValue(args.Length == 0 ? bot.PositionOf(_unit, null) : PositionOf(args, 0)),
        });

        _state.Register("GetHP", (_, args) => new[] { LuaValue.Of(bot.Health(_unit, SelfOr(args, 0))) });

        _state.Register("GetMaxHP", (_, args) =>
            new[] { LuaValue.Of(bot.MaxHealth(_unit, SelfOr(args, 0))) });

        // An object whose unit has already left the world reads as dead: the scripts hold their
        // targets as objects and test IsDead on them (TaskKillHero's HeroID is one captured at
        // init), so the answer has to outlive the world's lookup.
        _state.Register("IsDead", (_, args) => new[]
        {
            LuaValue.Of(args.Length == 0
                ? bot.IsDead(_unit, _unit)
                : UnitOf(args, 0) is not { } other || bot.IsDead(_unit, other)),
        });

        _state.Register("GetLastTookDamageTime", (_, args) => new[]
        {
            LuaValue.Of(bot.LastTookDamageTime(_unit, SelfOr(args, 0))),
        });

        _state.Register("GetMinions", (_, args) => new[]
        {
            ObjectList(bot.MinionsOf(_unit, Number(args, 0), Number(args, 1))),
        });

        _state.Register("GetHeroes", (_, args) => new[]
        {
            ObjectList(bot.HeroesOf(_unit, Number(args, 0))),
        });

        // GetStructures is the one list that hands out handles rather than objects; every entry
        // the scripts keep goes through GetObject before it is used as a unit.
        _state.Register("GetStructures", (_, args) => new[]
        {
            HandleList(bot.StructuresOf(_unit, Number(args, 0))),
        });

        _state.Register("GetObject", (_, args) => new[] { ObjectOf(UnitOfAny(args, 0)) });

        _state.Register("objGetName", (_, args) => new[] { LuaValue.Of(bot.NameOf(UnitOf(args, 0))) });

        _state.Register("GetTeam", (_, _) => new[] { LuaValue.Of(bot.TeamId(_unit)) });

        _state.Register("GetOtherTeam", (_, _) => new[] { LuaValue.Of(bot.OtherTeamId(_unit)) });

        // ---- the bot's own book ----

        _state.Register("GetRegroupPos", (_, _) => new[] { PointValue(bot.RegroupPos(_unit)) });

        _state.Register("GetMaxTravelDistSquared", (_, _) =>
            new[] { LuaValue.Of(bot.MaxTravelDistSquared(_unit)) });

        _state.Register("GetMinionDistanceToLane", (_, args) => new[]
        {
            LuaValue.Of(UnitOf(args, 0) is { } minion ? bot.MinionDistanceToLane(_unit, minion) : 0.0),
        });

        _state.Register("GetActiveTaskName", (_, args) => new[]
        {
            LuaValue.Of(bot.ActiveTaskName(_unit, args.Length > 0 ? UnitOf(args, 0) : _unit)),
        });

        _state.Register("GetAreAllBarracksStarted", (_, args) => new[]
        {
            LuaValue.Of(bot.AreAllBarracksStarted(_unit, Number(args, 0))),
        });

        _state.Register("GetTargetHealth", (_, _) => new[] { LuaValue.Of(bot.TargetHealth(_unit)) });
    }

    /// <summary>The list the scripts iterate: one object per unit, in the host's order.</summary>
    private LuaValue ObjectList(IReadOnlyList<Entity> units)
    {
        var list = new LuaTable();
        for (var index = 0; index < units.Count; index++)
            list.Set(LuaValue.Of(index + 1.0), ObjectOf(units[index]));
        return LuaValue.Of(list);
    }

    /// <summary>The handle list <c>GetStructures</c> hands out: net ids, not objects.</summary>
    private static LuaValue HandleList(IReadOnlyList<uint> netIds)
    {
        var list = new LuaTable();
        for (var index = 0; index < netIds.Count; index++)
            list.Set(LuaValue.Of(index + 1.0), LuaValue.Of((double)netIds[index]));
        return LuaValue.Of(list);
    }

    /// <summary>An optional object argument: the unit's own self when the scripts pass nothing.</summary>
    private Entity SelfOr(LuaValue[] args, int index) =>
        index < args.Length ? UnitOf(args, index) ?? _unit : _unit;

    /// <summary>A unit named either way the scripts name one: a handle (net id) or an object.</summary>
    private Entity? UnitOfAny(LuaValue[] args, int index)
    {
        if (index >= args.Length) return null;
        return args[index].Type == LuaType.Number
            ? _worldGet((uint)args[index].AsNumber)
            : UnitOf(args, index);
    }

    /// <summary>
    /// A position argument: the coordinate table the scripts build, or a unit when one is passed
    /// instead (the natives accept both shapes). A unit that has already left the world has no
    /// position and reads as the map origin.
    /// </summary>
    private Position2 PositionOf(LuaValue[] args, int index)
    {
        if (index >= args.Length || args[index].Type != LuaType.Table) return new Position2(0.0, 0.0);
        if (args[index].AsTable.Get(LuaValue.Of("netId")).ToNumber() is not null)
            return UnitOf(args, index)?.Position ?? new Position2(0.0, 0.0);
        return PointOf(args, index);
    }

    private static double DistanceSquared(Position2 first, Position2 second)
    {
        var x = first.X - second.X;
        var z = first.Z - second.Z;
        return x * x + z * z;
    }

    private static double Distance(Position2 first, Position2 second) =>
        Math.Sqrt(DistanceSquared(first, second));

    /// <summary>
    /// A point the scripts pass between natives. Nothing in Leashed builds one in Lua, so any
    /// round-tripping value would do; a table of coordinates is the readable one
    /// (<c>Reconstruction:</c> retail handed out a vector userdata).
    /// </summary>
    private static LuaValue PointValue(Position2 point)
    {
        var table = new LuaTable();
        table.Set(LuaValue.Of("x"), LuaValue.Of(point.X));
        table.Set(LuaValue.Of("z"), LuaValue.Of(point.Z));
        table.Set(LuaValue.Of(1.0), LuaValue.Of(point.X));
        table.Set(LuaValue.Of(2.0), LuaValue.Of(point.Z));
        return LuaValue.Of(table);
    }

    private static Position2 PointOf(LuaValue[] args, int index)
    {
        if (index >= args.Length || args[index].Type != LuaType.Table) return new Position2(0.0, 0.0);
        var table = args[index].AsTable;
        var x = table.Get(LuaValue.Of("x")).ToNumber() ?? table.Get(LuaValue.Of(1.0)).ToNumber() ?? 0.0;
        var z = table.Get(LuaValue.Of("z")).ToNumber() ?? table.Get(LuaValue.Of(2.0)).ToNumber() ?? 0.0;
        return new Position2(x, z);
    }

    private LuaValue ObjectOf(Entity? unit) =>
        unit is null ? LuaValue.Nil : LuaValue.Of(_objectFor(unit));

    private Entity? UnitOf(LuaValue[] args, int index)
    {
        if (index >= args.Length || args[index].Type != LuaType.Table) return null;
        var netId = args[index].AsTable.Get(LuaValue.Of("netId")).ToNumber();
        return netId is null ? null : _worldGet((uint)netId.Value);
    }

    /// <summary>An object's net id, read even when the unit has already left the world.</summary>
    private static uint NetIdOf(LuaValue[] args, int index)
    {
        if (index >= args.Length || args[index].Type != LuaType.Table) return 0u;
        var netId = args[index].AsTable.Get(LuaValue.Of("netId")).ToNumber();
        return netId is null ? 0u : (uint)netId.Value;
    }

    private static double Number(LuaValue[] args, int index) =>
        index < args.Length ? args[index].ToNumber() ?? 0.0 : 0.0;

    private static string Text(LuaValue[] args, int index) =>
        index < args.Length && args[index].Type == LuaType.String ? args[index].AsString : "";

}
