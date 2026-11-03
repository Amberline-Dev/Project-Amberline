using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Spells;

namespace Amberline.Simulation.Pets;

/// <summary>
/// The pet AI host: every pet runs its shipped script — <c>Pet.luaobj</c> (Tibbers and the clones),
/// <c>UncontrollablePet.luaobj</c> (Malzahar's voidlings) and <c>YorickPHPet.luaobj</c> (Yorick's
/// ghouls) — on one <see cref="LuaAiService"/> over <see cref="PetLuaAiEngine"/>. Nothing here
/// decides anything: the per-step pass runs the script's 0.15 s timers and then the native state's
/// own walk and swing, and the order path is the script's <c>OnOrder</c>.
/// <para>
/// This replaces the hand-written pet machine (<c>TibbersAiSystem</c>'s, <c>SharedCloneService</c>'s
/// and <c>YorickGhoulService</c>'s decision halves). What those got wrong is what the batch was for:
/// Pet.lua re-acquires at once in <c>OnTargetLost</c>, re-cancels the swing before every re-target,
/// and keeps its own near/far rule for a mirrored move.
/// </para>
/// </summary>
public sealed class PetAiService
{
    private readonly WorldState _world;
    private readonly CombatService _combat;
    private readonly ISpellBuffService? _buffs;

    public PetAiService(WorldState world, MovementSystem movement, CombatService combat,
        ISpellEffectSink effects, GameData? data = null, IMinionVisionService? vision = null,
        Func<Entity, Entity, bool>? canSee = null, IRandomSource? random = null,
        ISpellBuffService? buffs = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _buffs = buffs;
        Engine = new PetLuaAiEngine(world, movement, combat, effects, data, random, vision, canSee);
        Host = new LuaAiService(world, Engine);
    }

    /// <summary>The natives' world half, with the per-pet state and the route cache.</summary>
    public PetLuaAiEngine Engine { get; }

    /// <summary>The script host: one instance per pet, its timers and its callbacks.</summary>
    public LuaAiService Host { get; }

    /// <summary>The pets this host drives, for the checks.</summary>
    public IEnumerable<Entity> Pets => _world.Entities.Values
        .Where(unit => unit.Alive && unit.PetState is not null);

    /// <summary>The script's state ids and route cache, for the checks and the replays.</summary>
    public double? StateOf(Entity unit) => Engine.StateOf(unit);

    /// <summary>The target a pet last stood to attack, for the checks.</summary>
    public uint PlantedTargetOf(Entity unit) => Engine.PlantedTargetOf(unit);

    /// <summary>Where each pet's last path request aimed.</summary>
    public IReadOnlyDictionary<uint, Position2> LastDestinations => Engine.LastDestinations;

    /// <summary>
    /// The pet's next step runs its scripts' timers and keeps its phase from that step: the re-base
    /// the hand-written machines answered from a zeroed next scan (<c>TibbersAiSystem.ScanNow</c>,
    /// the controller buff's <c>NextAi = 0</c>). The checks that pin the 0.15 s cadence on a time
    /// axis of their own call it before their sequence.
    /// </summary>
    public void ScanNow(Entity unit) => Host.RephaseTimers(unit);

    /// <summary>
    /// The per-step pass: the bookkeeping the world owns, the expired lifetime, then each pet's
    /// script step (its <c>OnTargetLost</c> test and its due timers) and the native state's own walk
    /// and swing. <paramref name="now"/> is the combat clock the script's timers run on.
    /// </summary>
    public void Tick(double now, double dt)
    {
        if (!double.IsFinite(now) || !double.IsFinite(dt) || dt < 0.0)
            throw new ArgumentOutOfRangeException(nameof(dt));
        Engine.Tick(now, dt);
        // BBSpawnPet's Duration is a lifetime, so it expires against the combat clock the spawn
        // call used, not against the host's own step clock. A zero or infinite ExpiresAt is no
        // lifetime at all (the same reading LifecycleMonitor takes): a clone's and a ghoul's life
        // is their controller buff's, and a voidling's is infinite.
        //
        // The sweep is every owned pet the world holds, not only the ones a script drives: the
        // lifetime is the spawn call's, whatever the unit runs. Batch 146 narrowed it to the
        // PetState members and let a PetState-less pet outlive its ExpiresAt (the end-of-game
        // check's bare AnnieTibbers died only when something else killed it), so the sweep is back
        // to the retired TibbersAiSystem's predicate — EntityKind.Pet, an owner, alive — before any
        // GameOver gate, which is what keeps lifetime cleanup running through the ceremony.
        foreach (var pet in _world.Entities.Values.Where(unit => unit.Alive &&
                     (unit.PetState is not null || unit.Kind == EntityKind.Pet && unit.OwnerNetId != 0) &&
                     unit.ExpiresAt > 0.0 && double.IsFinite(unit.ExpiresAt) && now >= unit.ExpiresAt)
                     .ToArray())
        {
            _combat.Kill(pet, 0);
            Engine.Forget(pet.NetId);
        }
        foreach (var pet in Pets.ToArray())
        {
            // Reconstruction: the retail server stops deciding for a unit when the match is over;
            // the shipped scripts have no such hook, so the gate is the host's.
            if (_combat.GameOver) continue;
            SyncRevenantSpeed(pet);
            Host.StepUnit(pet, now, dt);
            // The shipped Pet.lua schedules its orders on its timers (TimerScanDistance and TimerFindEnemies, 0.15 s) and on its events:
            // the engine's order half runs only on a step one of those timers ran; the per-step half only swings.
            if (Host.TimerFired(pet)) Engine.Scan(pet);
            Engine.Continue(pet);
        }
    }

    /// <summary>
    /// The half of the retired clone machine the pet scripts do not carry: Yorick's revenant keeps
    /// the template's base speed and its own percentage effects, and only the owner's flat movement
    /// modifier follows him. It rides on the controller buff (the one whose CasterNetId is Yorick),
    /// so the pet's own speed is still the baseline plus its own buffs' flat parts - never the
    /// owner's capped total. Shaco keeps his spawn snapshot instead.
    /// </summary>
    private void SyncRevenantSpeed(Entity pet)
    {
        if (_buffs is null || pet.PetState is not { Kind: CloneKind.YorickRevenant } state) return;
        var name = CloneFamilies.ControllerBuff(state.Kind);
        var controller = name is null ? null : pet.Buffs.FirstOrDefault(buff => buff.Name == name);
        if (controller is null || _world.Get(controller.CasterNetId) is not { } caster) return;
        var difference = caster.FlatMovementSpeedMod - pet.Buffs.Sum(buff => buff.Payload.MoveSpeedFlat);
        if (difference != 0.0) controller.Payload.MoveSpeedFlat += difference;
        _buffs.RefreshMovementSpeed(pet);
    }

    /// <summary>
    /// A pet control packet (the 0x75 hard orders 5/6/9/11): <c>ORDER_PETHARDATTACK</c>,
    /// <c>ORDER_PETHARDMOVE</c>, <c>ORDER_PETHARDRETURN</c> and <c>ORDER_PETHARDSTOP</c>. The order
    /// goes to the named pet, or to every pet of the owner when the caller names none. Answers
    /// whether a pet's script took it.
    /// </summary>
    public bool Command(Entity owner, int order, Position2 position, uint? targetNetId = null,
        uint? actorNetId = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (WireOrder(order) is not { } scriptOrder) return false;
        if (order == 6 && (!double.IsFinite(position.X) || !double.IsFinite(position.Z))) return false;
        var accepted = false;
        foreach (var pet in OwnedPets(owner, actorNetId))
        {
            var target = targetNetId is uint id ? _world.Get(id) : null;
            if (Deliver(pet, scriptOrder, position, target)) accepted = true;
        }
        return accepted;
    }

    /// <summary>
    /// The owner's ordinary order mirrored onto his pet by the client (a pet-actor 0x75 of order
    /// 1/2/3/7): <c>ORDER_HOLD</c>, <c>ORDER_MOVETO</c>, <c>ORDER_ATTACKTO</c> or
    /// <c>ORDER_ATTACKMOVE</c>. Pet.lua's own <c>OnOrder</c> decides what it does with it — a hard
    /// state swallows most of them, and a mirrored move is near or far against the click point —
    /// and this answers its return.
    /// </summary>
    public bool Order(Entity owner, uint actorNetId, int order, Position2 click,
        uint? targetNetId = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (WireOrder(order) is not { } scriptOrder) return false;
        if (!double.IsFinite(click.X) || !double.IsFinite(click.Z)) return false;
        var pet = _world.Get(actorNetId);
        if (pet is null || !pet.Alive || pet.PetState is null || pet.OwnerNetId != owner.NetId)
            return false;
        var target = targetNetId is uint id ? _world.Get(id) : null;
        return Deliver(pet, scriptOrder, click, target);
    }

    /// <summary>
    /// Runs one order in a pet's script. The in-position point is the click before the call: that
    /// is what <c>ORDER_MOVETO</c>'s near/far rule reads (<c>DistanceBetweenObjectAndInPosSq(me)</c>
    /// against the script's own <c>FAR_MOVMEMENT_DISTANCE</c>) and where <c>ORDER_PETHARDMOVE</c>
    /// walks to.
    /// </summary>
    private bool Deliver(Entity pet, double scriptOrder, Position2 click, Entity? target)
    {
        Engine.SetLastPosPoint(pet, click);
        return Host.Order(pet, scriptOrder, target);
    }

    private IEnumerable<Entity> OwnedPets(Entity owner, uint? actorNetId) =>
        _world.Entities.Values.Where(unit => unit.Alive && unit.PetState is not null &&
            unit.OwnerNetId == owner.NetId && (actorNetId is null || unit.NetId == actorNetId.Value));

    /// <summary>
    /// The 0x75 order id a pet actor may carry into Pet.lua's <c>ORDER_*</c> constant
    /// (<see cref="AiGlobals"/>). The session rejects every other value for a pet actor before the
    /// dispatch, and <c>ORDER_STOP</c> (the hero's own stop) is one of them: the mirrored stop is
    /// not pet control.
    /// </summary>
    public static double? WireOrder(int order) => order switch
    {
        1 => 204.0,   // ORDER_HOLD -> AI_PET_HOLDPOSITION
        2 => 202.0,   // ORDER_MOVETO -> AI_PET_MOVE (near/far rule in the script)
        3 => 201.0,   // ORDER_ATTACKTO -> AI_PET_ATTACK on the target
        7 => 200.0,   // ORDER_ATTACKMOVE -> AI_PET_ATTACKMOVE
        5 => 206.0,   // ORDER_PETHARDATTACK -> AI_PET_HARDATTACK
        6 => 207.0,   // ORDER_PETHARDMOVE -> AI_PET_HARDMOVE to the click
        9 => 208.0,   // ORDER_PETHARDRETURN -> AI_PET_HARDRETURN to the owner
        11 => 209.0,  // ORDER_PETHARDSTOP -> AI_PET_HARDSTOP
        _ => null,
    };
}
