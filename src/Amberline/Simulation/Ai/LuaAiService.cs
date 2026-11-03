using Amberline.Core;
using Amberline.Scripting.Lua;
using Amberline.Simulation.Pets;
using LuaValue = Amberline.Scripting.Lua.LuaValue;

namespace Amberline.Simulation.Ai;

/// <summary>
/// One match's Lua AI host: a script instance per AI unit, the unit's event callbacks and its
/// timers. The engine raises events here; the host runs the unit's script and the script talks
/// back through the natives, which are bound to the services that already own targeting, combat
/// and movement (<see cref="ILuaAiEngine"/>).
/// Turret.luaobj, Leashed.luaobj and Hero.luaobj run this way: a turret, every jungle monster and
/// every champion. The host
/// carries what the rest of the corpus needs (per-unit states, named repeating and one-shot
/// timers, the callback set, the script loaders); the natives a script names are bound when the
/// unit's engine carries that half (<see cref="ILuaAiLeashedEngine"/> for the jungle), so a
/// missing native is a real error rather than a silent no-op.
/// </summary>
public sealed class LuaAiService : IAiHost
{
    private readonly WorldState _world;
    private readonly ILuaAiEngine _engine;
    private readonly AiScriptCatalog _catalog;
    private readonly Dictionary<uint, LuaAiInstance> _instances = new();
    private readonly Dictionary<uint, LuaTable> _objects = new();
    private readonly Dictionary<string, LuaAiScript> _scripts = new(StringComparer.OrdinalIgnoreCase);

    public LuaAiService(WorldState world, ILuaAiEngine engine, AiScriptCatalog? catalog = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _catalog = catalog ?? new AiScriptCatalog();
    }

    /// <summary>Where a script failure goes. The composition root points this at the session log.</summary>
    public Action<AiScriptFailure>? OnError { get; set; }

    /// <summary>Every live instance, in creation order, for the checks.</summary>
    public IEnumerable<LuaAiInstance> Instances => _instances.Values;

    /// <summary>
    /// Which champions are bot seats', asked before a hero is claimed. A bot seat's hero runs
    /// <c>Bot.luaobj</c> on the bot host and no <c>Hero.luaobj</c>, and a human player's hero runs
    /// <c>Hero.luaobj</c> on the hero host and no <c>Bot.luaobj</c> (batch 167) — the family a
    /// champion belongs to is its seat's, not the unit's. The composition root points both hosts at
    /// the bot controller's answer; unset (every harness that runs one family alone) each host
    /// drives the heroes it is handed, which is what each batch's own checks assume.
    /// </summary>
    public Func<Entity, bool>? IsBotHero { get; set; }

    /// <summary>
    /// The instance driving a unit, creating it (and running its <c>OnInit</c>) on first use.
    /// Reconstruction: retail built the instance when the AI unit was created, not at its first
    /// step. The host creates it lazily because that is where the engine already meets every AI
    /// unit (spawn services have no handle on it); a timer is armed one interval after the step
    /// the host first observed it in either way, so the two timings are indistinguishable in play.
    /// A late birth must then not let the chunk's own <c>OnInit</c> undo what the unit already
    /// carries — the rule <see cref="Minions.MinionAiSystem"/> applies to a lane minion's late
    /// birth: the timers are the birth's job, the order is not. Hero.txt's OnInit (proto 0.1
    /// pc 0000-0004) clears the in-position point and writes AI_IDLE, which the hero engine answers
    /// with the attack-move-goal drop and a stop of the live route (and a stop path on the wire);
    /// retail ran that at the champion's creation, before any order could exist. So the order is
    /// taken off the unit for the call and put back after it.
    /// </summary>
    public LuaAiInstance? InstanceFor(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (_instances.TryGetValue(unit.NetId, out var existing)) return existing;
        if (ScriptNameFor(unit) is not { } name) return null;
        var carried = CarryOrderAside(unit);
        var instance = new LuaAiInstance(unit, Script(name), _engine, _catalog, ObjectFor, _world.Get,
            failure => OnError?.Invoke(failure), _world.LuaRandom);
        _instances[unit.NetId] = instance;
        instance.Call("OnInit");
        RestoreOrder(unit, carried);
        return instance;
    }

    /// <summary>
    /// The live order, taken off the unit while its chunk is born. The route goes with it (a fresh
    /// list, not a clear) so the <c>SetState</c> the birth runs sees a unit that is not moving and
    /// sends no stop path.
    /// </summary>
    private static CarriedOrder CarryOrderAside(Entity unit)
    {
        var carried = new CarriedOrder(unit.Waypoints, unit.Target, unit.AttackMoveGoal,
            unit.HeroPursuit, unit.LastAttackStopPoint);
        unit.Waypoints = new List<Position2>();
        unit.Target = null;
        unit.AttackMoveGoal = null;
        unit.HeroPursuit = null;
        unit.LastAttackStopPoint = null;
        return carried;
    }

    private static void RestoreOrder(Entity unit, CarriedOrder carried)
    {
        unit.Waypoints = carried.Waypoints;
        unit.Target = carried.Target;
        unit.AttackMoveGoal = carried.AttackMoveGoal;
        unit.HeroPursuit = carried.HeroPursuit;
        unit.LastAttackStopPoint = carried.LastAttackStopPoint;
    }

    /// <summary>The walk and the order intent a unit carries across its chunk's birth.</summary>
    private readonly record struct CarriedOrder(List<Position2> Waypoints, Position2? Target,
        Position2? AttackMoveGoal, HeroPursuitState? HeroPursuit, Position2? LastAttackStopPoint);

    /// <summary>
    /// The instance already driving a unit, without creating one. A bot asks for a teammate's
    /// active task name (<c>GetActiveTaskName</c>), and a teammate of a bot seat can be a human
    /// whose hero must never get a script instance of its own.
    /// </summary>
    public LuaAiInstance? ExistingInstanceFor(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return _instances.GetValueOrDefault(unit.NetId);
    }

    /// <summary>Whether this host drives the unit.</summary>
    public bool Drives(Entity unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return ScriptNameFor(unit) is not null;
    }

    /// <summary>The engine's per-tick pass: its target-lost test, then the unit's due timers.</summary>
    public void StepUnit(Entity unit, double now, double dt)
    {
        var instance = InstanceFor(unit);
        if (instance is null) return;
        ReportLostTarget(unit, instance);
        instance.Advance(now, dt);
    }

    /// <summary>Whether the unit's script ran one of its timers in the step just taken (<see cref="LuaAiInstance.TimerFiredThisStep"/>).</summary>
    public bool TimerFired(Entity unit) => ExistingInstanceFor(unit)?.TimerFiredThisStep ?? false;

    /// <summary>Re-bases a unit's script timers onto the next step (<see cref="LuaAiInstance.RephaseTimers"/>).</summary>
    public void RephaseTimers(Entity unit) => InstanceFor(unit)?.RephaseTimers();

    /// <summary>
    /// <c>OnTargetLost(lossReason, lostObject)</c> for the target the unit's AI is holding, once per
    /// loss. The id can already be gone — the world or the combat tick retires a dead target's id
    /// before this pass — and Leashed reads the lost object (a dead pet's owner) all the same, so
    /// the loss is read from <see cref="ILuaAiEngine.HeldTargetId"/> rather than looked up. The
    /// script is notified before the engine drops an id it still holds, so OnTargetLost's own
    /// SetStateAndCloseToTarget/FindNewTarget is the replacement and only an unreplaced loss is
    /// cleared. A target the script itself dropped is not a loss (Leashed' Retreat clears it), and
    /// a loss is reported once: the id is not held again unless the script takes a different
    /// target. Reconstruction: whether retail cleared first (and so what GetTarget answered during
    /// the callback) is unrecovered; Turret.luaobj never asks.
    /// The pair is <c>(lossReason, lostObject)</c> — Hero.txt's OnTargetLost (proto 0.3 params=2)
    /// compares the first with <c>LOST_VISIBILITY</c> and passes the second to
    /// SetStateAndCloseToTarget; Leashed' (proto 0.6 params=2) reads only the second, and Turret's,
    /// minionLib's and Pet's take no parameters at all, so the shape is the same for every shipped
    /// chunk.
    /// </summary>
    private void ReportLostTarget(Entity unit, LuaAiInstance instance)
    {
        if (_engine.HeldTargetId(unit) is not uint lostId) return;
        var held = _world.Get(lostId);
        if (held is not null && _engine.TargetStillValid(unit, held)) return;
        // Each family keeps the callback its own script defines: the name from the engine
        // (Bot.luaobj's OnLostTarget forwards to the active task's OnTargetLost; every other chunk
        // names OnTargetLost) and the arguments that proto takes. Hero.txt's (proto 0.3, params=2)
        // is (lossReason, lostObject) — it compares the first with LOST_VISIBILITY and passes the
        // second to SetStateAndCloseToTarget — and Leashed' (proto 0.6, params=2) reads the second;
        // Bot.luaobj's own handler is params=0 and so is every other OnTargetLost, so the object
        // pair the bot host has always passed is inert there and the (reason, lost) pair of the
        // other hosts leaves both shapes' second argument where those protos read it.
        if (_engine is ILuaAiBotEngine)
            instance.Call(_engine.LostTargetHook, LuaValue.Of(ObjectFor(unit)), LuaValue.Of(ObjectFor(lostId)));
        else
            instance.Call(_engine.LostTargetHook, LuaValue.Of(LossReason(unit, held)), LuaValue.Of(ObjectFor(lostId)));
        // Only a loss the script did not answer with its own SetTarget is cleared here.
        if (_engine.HeldTargetId(unit) == lostId) _engine.SetTarget(unit, null);
    }

    /// <summary>What this loss is reported as: <c>LOST_VISIBILITY</c> for a target only sight left.</summary>
    private double LossReason(Entity unit, Entity? lost) =>
        _engine is ILuaAiHeroEngine hero ? hero.TargetLossReason(unit, lost) : 0.0;

    /// <summary>Raises one engine event in the unit's script.</summary>
    private void Dispatch(Entity unit, string hook, params LuaValue[] arguments) =>
        InstanceFor(unit)?.Call(hook, arguments);

    /// <summary>
    /// <c>OnOrder(order, target)</c>, one order from the owner's click or a pet control packet
    /// (<c>ORDER_*</c>, already mapped from the wire id by the caller). The script answers whether
    /// it took the order; a false or a nil is a rejection the dispatch reports to the client. The
    /// engine's in-position point is the caller's to fill first (the click), because that is what
    /// the order's near/far rule and the hard move read.
    /// </summary>
    public bool Order(Entity unit, double order, Entity? target) =>
        InstanceFor(unit) is { } instance &&
        instance.Call("OnOrder", LuaValue.Of(order), ObjectOf(target)).Truthy;

    /// <summary>
    /// <c>OnTargetLost</c> when the caller has no object for what was lost: the script reads nil
    /// for it and a zero reason (not a visibility loss) and falls through to <c>FindNewTarget</c>.
    /// The retained-target pass above is the one that carries the lost object.
    /// </summary>
    public void OnTargetLost(Entity unit) => Dispatch(unit, "OnTargetLost", LuaValue.Of(0.0), LuaValue.Nil);

    /// <summary>
    /// <c>OnTargetLost</c> with the unit that was lost, which is what Leashed reads (proto 0.6
    /// passes it to GetOwner/GetGoldRedirectTarget). Hero's proto 0.3 also reads the reason.
    /// </summary>
    public void OnTargetLost(Entity unit, Entity? lost) =>
        Dispatch(unit, "OnTargetLost", LuaValue.Of(LossReason(unit, lost)), ObjectOf(lost));

    /// <summary>
    /// <c>OnReachedDestinationForGoingToLastLocation</c>: the walk to a lost target's last known
    /// position arrived without the target coming back. minionLib exports the hook (proto 0.12)
    /// and Hero.luaobj's own TimerDistanceScan body ends the walk the same way; the scripts take
    /// no argument.
    /// </summary>
    public void OnReachedDestinationForGoingToLastLocation(Entity unit) =>
        Dispatch(unit, "OnReachedDestinationForGoingToLastLocation");

    public void OnCallForHelp(Entity unit, Entity aggressor) =>
        // The first argument is the callee: minionLib (proto 0.3) and Turret (proto 0.2) read only the second argument
        // (the aggressor), and no shipped script reads the first. The shape matches minionLib's (self, aggressor).
        // Leashed names the same event LeashedCallForHelp and takes the same pair.
        Dispatch(unit, CallForHelpHook(unit),
            LuaValue.Of(ObjectFor(unit)), LuaValue.Of(ObjectFor(aggressor)));

    public void OnTakeDamage(Entity unit, Entity attacker) =>
        Dispatch(unit, "OnTakeDamage", LuaValue.Of(ObjectFor(attacker)));

    public void OnFearBegin(Entity unit) => Dispatch(unit, "OnFearBegin");

    public void OnFearEnd(Entity unit) => Dispatch(unit, "OnFearEnd");

    public void OnTauntBegin(Entity unit) => Dispatch(unit, "OnTauntBegin");

    public void OnTauntEnd(Entity unit) => Dispatch(unit, "OnTauntEnd");

    public void OnCanMove(Entity unit) => Dispatch(unit, "OnCanMove");

    public void OnCanAttack(Entity unit) => Dispatch(unit, "OnCanAttack");

    public void HaltAI(Entity unit) => Dispatch(unit, "HaltAI");

    /// <summary>
    /// <c>OnPathToTargetBlocked</c>: the planner could not reach the held target at all. The
    /// scripts take no argument (minionLib 0.2); the host raises it once the approach has
    /// exhausted its angles.
    /// </summary>
    public void OnPathToTargetBlocked(Entity unit) => Dispatch(unit, "OnPathToTargetBlocked");

    /// <summary>
    /// <c>OnCollisionEnemy(other)</c>: a completed step ran this unit into an enemy body.
    /// minionLib 0.10 takes the one collided body and nothing else, so this dispatches that one
    /// argument — a leading <c>self</c> would bind the script's <c>other</c> to its own unit.
    /// </summary>
    public void OnCollisionEnemy(Entity unit, Entity other) =>
        Dispatch(unit, "OnCollisionEnemy", ObjectOf(other));

    /// <summary>
    /// <c>OnCollisionOther(other)</c>: a completed step ran this unit into something that is not
    /// an enemy body (minionLib 0.11, same one-argument shape).
    /// </summary>
    public void OnCollisionOther(Entity unit, Entity other) =>
        Dispatch(unit, "OnCollisionOther", ObjectOf(other));

    /// <summary>
    /// Which shipped script a unit runs on this host. A host drives the units its engine can bind
    /// the natives for: the jungle host (whose engine carries <see cref="ILuaAiLeashedEngine"/>) runs
    /// every camp member's Leashed.luaobj, the pet host (whose engine carries
    /// <see cref="ILuaAiPetEngine"/>) runs every pet's shipped pet script, the minion host (whose
    /// engine carries <see cref="ILuaAiMinionEngine"/>) runs every lane minion's Minion.luaobj and
    /// its minionLib, the bot host (whose engine carries <see cref="ILuaAiBotEngine"/>) runs
    /// Bot.luaobj on the seats it is given, and every other host runs Turret.luaobj and nothing else.
    /// BuffLifecycle raises each control event to every AI host, so a host that claimed a unit it
    /// cannot bind would both run that unit's AI a second time and raise a missing-native failure
    /// on its first callback. A champion follows the same rule: the hero host (whose engine carries
    /// <see cref="ILuaAiHeroEngine"/>) runs Hero.luaobj for the champions the bot scripts do not
    /// drive, the bot host (whose engine carries <see cref="ILuaAiBotEngine"/>) runs Bot.luaobj for
    /// the ones they do, and a spawned unit whose pet script is the one driving it is left to the
    /// pet host. The lane-less units a <c>BBSpawnMinion</c> gave <c>idle.lua</c>, <c>Aggro.lua</c> or <c>Minion.lua</c>
    /// run their own chunks on the spawned host (whose engine carries
    /// <see cref="ILuaAiSpawnedEngine"/>), which is the only one that claims them: a ward is not a
    /// turret and a Nimbus clone is not a lane minion. Batch 662: the capture point guardians' host
    /// (whose engine carries <see cref="ILuaAiTurretScriptEngine"/>) runs Turret.luaobj on them.
    /// </summary>
    private string? ScriptNameFor(Entity unit) => unit.UnitType switch
    {
        _ when _engine is ILuaAiSpawnedEngine => SpawnedUnitAi.HostScript(unit.AiScript),
        _ when _engine is ILuaAiTurretScriptEngine => "Turret",
        UnitType.Turret when _engine is not ILuaAiLeashedEngine and not ILuaAiPetEngine => "Turret",
        // Batch 451: an Odin wave unit runs its shipped lane script (MinionOdin or MinionReverse) on the lane host.
        UnitType.Minion when _engine is ILuaAiMinionEngine && Amberline.Simulation.Minions.OdinWaves.ScriptOf(unit) is { } odin => odin,
        UnitType.Minion when _engine is ILuaAiMinionEngine && unit.MinionType is not null => "Minion",
        UnitType.Hero when unit.PetState is null && _engine is ILuaAiHeroEngine &&
            (IsBotHero is null || !IsBotHero(unit)) => "Hero",
        UnitType.Hero when _engine is ILuaAiBotEngine &&
            (IsBotHero is null || IsBotHero(unit)) => "Bot",
        _ when unit.Kind == EntityKind.Neutral && _engine is ILuaAiLeashedEngine => "Leashed",
        _ when _engine is ILuaAiPetEngine => SpawnedUnitAi.PetHostScript(unit.PetState),
        _ => null,
    };

    /// <summary>The script's own name for the call-for-help event.</summary>
    private static string CallForHelpHook(Entity unit) =>
        unit.Kind == EntityKind.Neutral ? "LeashedCallForHelp" : "OnCallForHelp";

    private LuaValue ObjectOf(Entity? unit) =>
        unit is null ? LuaValue.Nil : LuaValue.Of(ObjectFor(unit));

    private LuaAiScript Script(string name)
    {
        if (_scripts.TryGetValue(name, out var script)) return script;
        script = new LuaAiScript(name, _catalog.Load(name));
        _scripts[name] = script;
        return script;
    }

    /// <summary>
    /// A unit's script object: the table the natives hand back for it. The host has no userdata
    /// type, so every unit is a table; the scripts only compare and pass these, so identity is
    /// all they need. <c>netId</c> is what the natives read back.
    /// </summary>
    private LuaTable ObjectFor(Entity unit) => ObjectFor(unit.NetId);

    /// <summary>The object table for a unit id, kept after its unit has left the world.</summary>
    private LuaTable ObjectFor(uint netId)
    {
        if (_objects.TryGetValue(netId, out var existing)) return existing;
        var table = new LuaTable();
        table.Set(LuaValue.Of("netId"), LuaValue.Of((double)netId));
        _objects[netId] = table;
        return table;
    }
}
