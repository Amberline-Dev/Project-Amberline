using System;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.BehaviorTrees;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The non-champion units' script owner (batch 159 step 2): the shipped <c>DATA/Characters/&lt;unit&gt;/Scripts/
/// CharScript&lt;unit&gt;.luaobj</c> of the map's own units runs from its own code, bound to one unit at a time the way a
/// champion's character script is bound to a champion. One module for every such unit, not one per character — the same
/// split <see cref="BbItemModule"/> makes for the items and <see cref="BbSummonerModule"/> for the summoner spells: a unit
/// character script is not a champion script, and a match binds them for units no hero owns.
/// <para>
/// The module owns its own <see cref="BbSpellHost"/>, <see cref="BbHookRouter"/> and <see cref="BbBuffRuntime"/> so the
/// buffs its units' scripts add cannot collide with a champion module's slots 0..3 or an item copy's. The router's
/// per-unit binding (<see cref="BbHookRouter.BindUnit"/>) is what makes <c>CharOnActivate</c>,
/// <c>UpdateSelfBuffStats</c>/<c>Actions</c>, <c>CharOnHitUnit</c>, <c>CharOnBeingHit</c> and <c>CharOnPreDamage</c>
/// run for that unit; the gameplay hooks admit it through <see cref="IBbHookSink.OwnsUnit"/>.
/// </para>
/// <para>
/// Batch 159 step 2 binds the turret characters of Summoner's Rift. A character with no shipped script is not this
/// module's business (<see cref="BindUnit"/> answers false) and keeps the hand-written path — the one map unit that has
/// none is <c>OrderTurretShrine</c>. What stays with the engine is what no shipped script decides: targeting and the
/// attack itself, the wire, the protection chain; everything the script states is the script's.
/// </para>
/// </summary>
public sealed class BbUnitCharacterModule
{
    private readonly GameData _data;
    private readonly BbScriptSet _scripts;
    private readonly Dictionary<Entity, string> _bound = new(ReferenceEqualityComparer.Instance);
    private BbSpellHost? _host;
    private BbHookRouter? _router;

    public BbUnitCharacterModule(GameData? data = null)
    {
        _data = data ?? GameData.Default;
        // The scripts are not closure members: each unit's own CharScript is read from the shipped file the first time a
        // unit of that character is bound (BbScriptSet.SpawnedCharacter), the layout the client itself uses
        // (Characters/<skin>/Scripts/CharScript<skin>.luaobj). A closure that listed every character would load all of
        // them for a match that binds one.
        _scripts = new BbScriptSet(new BbClosure("UnitCharacters", Array.Empty<BbClosureScript>(),
            Array.Empty<BbDanglingReference>(), Array.Empty<string>(), Array.Empty<BbDiagnostic>()), _data.Paths);
    }

    /// <summary>Batch 446: spawns an encounter's unit of <paramref name="skin"/> through this module's host (null before the module is bound).</summary>
    internal Entity? SpawnEncounterUnit(string skin, Position2 position, Team team, UnitType unitType = UnitType.Monster) =>
        _host?.SpawnEncounterUnit(skin, position, team, unitType);

    /// <summary>Binds the module to a match. Call once per match.</summary>
    public IBbHookSink Bind(SpellSystem system, Action<BbScriptFailure>? failureSink = null)
    {
        if (_host is not null) throw new InvalidOperationException("the module is already bound to a match");
        // A unit has no champion spellbook, so its slots answer no spell name (the item module's slot lookup is the copy's
        // own id; a unit character script's casts name their spells outright).
        var host = new BbSpellHost(system, (_, _) => "");
        host.ChildCaster = CastChild;
        var router = new BbHookRouter(host, _scripts);
        host.Router = router;
        router.FailureSink = failureSink;
        host.Buffs = new BbBuffRuntime(host, _scripts, router);
        _host = host;
        _router = router;
        return router;
    }

    public bool IsBound => _host is not null;

    /// <summary>
    /// Batch 563: one BuildingBlocks native the map's own Lua calls. The Basic Tutorial's TutorialScript loads the shipped
    /// <c>DATA/BuildingBlocks/BBLuaConversionLibrary.luaobj</c> (OnTutorialInit pc 0000-0002), whose wrappers (SpawnMinion,
    /// TutorialSpellBuffAdd, TeleportToPosition, ...) fill <c>passThroughParams</c> and <c>perBlockParams</c> and call <c>BBSpawnMinion</c>,
    /// <c>BBSpellBuffAdd</c>, <c>BBTeleportToPosition</c> and their siblings with them: exactly a block's scope and its parameters. They run
    /// here, in this module's host, the one that owns the map's own units and their buffs. <paramref name="vars"/> is the scope's root table
    /// (the natives write their outputs, <c>DestObj</c>, <c>BubbleIDVar</c>, into it); <paramref name="parameters"/> holds the block's scalar
    /// parameters. Reconstruction: the hook's own unit (<paramref name="owner"/>) is the engine's choice, the shipped files name none.
    /// </summary>
    internal void RunLevelBlock(string function, BbTable parameters, BbTable vars, Entity? owner)
    {
        var host = _host ?? throw new InvalidOperationException("BbUnitCharacterModule.Bind was not called");
        var fields = parameters.Entries.Where(entry => entry.Key is string)
            .Select(entry => new BbField((string)entry.Key, entry.Value switch
            {
                string text => new BbValue.Text(text),
                double number => new BbValue.Number(number),
                bool flag => new BbValue.Bool(flag),
                _ => (BbValue)new BbValue.Nil(),
            })).ToArray();
        host.Push(new BbHookFrame("LevelScript", "TutorialScript", owner));
        try
        {
            new BbInterpreter(host).Dispatch(new BbBlock(function, fields, Array.Empty<BbBlock>()), new BbScope(vars));
        }
        finally
        {
            host.Pop();
        }
    }

    /// <summary>The script buff instances this module's units added (the same handle the champion modules keep).</summary>
    internal BbBuffRuntime? Buffs => _host?.Buffs;

    /// <summary>Hook runs that raised and were skipped (production safety); empty in a healthy match.</summary>
    public IReadOnlyList<BbScriptFailure> ScriptFailures => _router?.Failures ?? Array.Empty<BbScriptFailure>();

    /// <summary>The script missiles this module's units have in flight (the registry BBDestroyMissile reads); the checks follow them.</summary>
    internal IReadOnlyList<BbMissileFlight> LiveMissiles => _router?.Missiles.Live ?? Array.Empty<BbMissileFlight>();

    private BbHookRouter RouterOrThrow => _router ?? throw new InvalidOperationException("BbUnitCharacterModule.Bind was not called");

    /// <summary>Whether the shipped data holds a character script for that character (a turret's, a camp's).</summary>
    public bool HasScript(string character) => character.Length > 0 && _scripts.SpawnedCharacter(character) is not null;

    /// <summary>The shipped file a character's script is read from (a check's pin; null when it has none).</summary>
    internal string? SpawnedCharacterName(string character) => _scripts.SpawnedCharacterPath(character);

    /// <summary>
    /// Binds one unit to the character script of <paramref name="character"/> and runs its hooks (retail runs
    /// <c>CharOnActivate</c> when the character is created, the same rule as a hero). False when the character has no
    /// shipped script: the unit keeps the hand-written path and is never claimed by <see cref="Poll"/> or the hook dispatch.
    /// <para>
    /// The bind also takes the character's first <c>UpdateSelfBuffStats</c>/<c>UpdateSelfBuffActions</c> sample, because a
    /// new character's stat accumulators are built when it is created — the same sentence
    /// <see cref="BbHookRouter.BindUnit"/> gives <c>CharOnActivate</c>. That is what puts a camp monster's
    /// <c>CrestoftheAncientGolem</c>/<c>HPByPlayerLevel</c> (CharScriptAncientGolem 0000-0075) and a siege minion's
    /// <c>TurretShield</c> (CharScriptBlue_Minion_MechCannon 0000-0038) on the unit before the spawn burst wires them;
    /// <see cref="Poll"/> keeps the cadence from the next spell update on. The sample runs before
    /// <see cref="BbHookRouter.RunCharOnActivate"/>: the shipped dragon adds <c>HPByPlayerLevel</c> from its stats hook
    /// (CharScriptDragon 0000-0046) and <c>ResistantSkinDragon</c> from its activate (0109-0155), and the stock client is
    /// born with them in that order, Internal in slot 0 and the aura in slot 1. A character that adds its buffs from one
    /// hook alone is order-equivalent either way (CharScriptAncientGolem's own is <c>UpdateSelfBuffActions</c>,
    /// 0000-0075; CharScriptWraith's <c>UpdateSelfBuffStats</c>, 0000-0043; CharScriptBlue_Minion_MechMelee's
    /// <c>CharOnActivate</c>, 0054-0096).
    /// </para>
    /// </summary>
    public bool BindUnit(Entity unit, string character)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!HasScript(character)) return false;
        return BindUnit(unit, character, _scripts.SpawnedCharacter(character)!);
    }

    /// <summary>
    /// Binds one unit to a character script that was loaded somewhere else (a check's probe), under the name the rest of
    /// the match knows it by. The shipped form above is the production path; this is the seam a hand-built character
    /// takes to reach the same router, hooks and poll.
    /// </summary>
    internal bool BindUnit(Entity unit, string character, BbScript script)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(script);
        _bound[unit] = character;
        // The claim the hand-written rules read (Entity.BoundCharacter): from here the shipped script is the unit's
        // character, and every rule it states is its own alone.
        unit.BoundCharacter = character;
        RouterOrThrow.BindUnit(unit, script);
        // The first self-buff sample first, CharOnActivate last (the order the shipped dragon's own hooks are written in,
        // and the order the client is born with: HPByPlayerLevel slot 0, ResistantSkinDragon slot 1). Batch 256: this sample is
        // the first poll of the unit's self-buff line, which Poll continues.
        _host!.Spells.StepSelfUpdate(unit);
        RouterOrThrow.RunUpdateSelfBuffStats(unit);
        RouterOrThrow.RunUpdateSelfBuffActions(unit);
        RouterOrThrow.RunCharOnActivate(unit, script);
        return true;
    }

    /// <summary>The character a unit is bound to (a check's pin), or null when it is not bound.</summary>
    public string? CharacterOf(Entity unit) => _bound.TryGetValue(unit, out var character) ? character : null;

    /// <summary>Every (unit, character) pair bound now, in bind order.</summary>
    public IReadOnlyList<(Entity Unit, string Character)> Bound => _bound.Select(pair => (pair.Key, pair.Value)).ToArray();

    /// <summary>The unit's own <c>CharVars</c>, the table its character script's hooks run with.</summary>
    public BbTable CharVars(Entity unit) => RouterOrThrow.CharVars(unit);

    /// <summary>
    /// The unit is gone (died, despawned): its character script binding and its per-unit tables go with it. Its buffs are
    /// the buff lifecycle's own to remove.
    /// </summary>
    public void Unbind(Entity unit)
    {
        if (!_bound.Remove(unit)) return;
        unit.BoundCharacter = null;
        _host?.Spells.ForgetSelfUpdate(unit);
        RouterOrThrow.UnbindUnit(unit);
    }

    /// <summary>
    /// One spell update of every bound unit: <c>UpdateSelfBuffStats</c> first, then <c>UpdateSelfBuffActions</c> — the order
    /// one champion tick has (<see cref="BbChampionModule.Tick"/>: retail rebuilds the stat accumulators before the
    /// per-update actions). A dead unit's stats hook is skipped inside the router (the same Reconstruction as a hero's);
    /// its actions hook runs and the script guards on <c>IS_NOT_DEAD</c> itself.
    /// <para>
    /// Batch 256: each unit polls on its own self-buff line (<paramref name="due"/>, <c>SpellSystem.StepSelfUpdate</c>; <see cref="BbSelfUpdate"/>:
    /// a bare hook every 0.25 s, a hook that owns a periodic every update), the hero's rule. Reconstruction for the period, as the
    /// hero's: the layout puts the server's update throttle (<c>srvNextUpdate</c>) and the character script (<c>CharScript</c>)
    /// on <c>obj_AI_Base</c>, the class a hero, a turret, a minion and a monster share, so
    /// none of them updates its script on a cadence of its own. CharScriptWorm's stats hook (0039-0057) writes <c>SetCanAttack</c>
    /// and <c>SetCanMove</c> false on every poll; the status natives write one bit of CharacterState's word with no per-update reset
    /// so another script's rest write holds until the next poll in retail as here: at most one
    /// line. A null <paramref name="due"/> is a manual poll of every unit (the checks' own).
    /// </para>
    /// </summary>
    public void Poll(Func<Entity, bool>? due = null)
    {
        // Batch 174c: a displacement a unit's own script started (the Baron's Propel knocks a hero up from the geyser's placemarker) is
        // registered in this module's host. Only the champion modules tick their host's displacements (BbChampionModule.TickSharedProjectilePhase),
        // so here the move completed and was never released: ForcedMove stayed attached to the hero after its landing.
        _host?.Moves.Tick();
        if (_router is null || _bound.Count == 0) return;
        foreach (var (unit, character) in _bound.ToArray())
        {
            var onLine = due?.Invoke(unit) ?? true;
            if (onLine) _pollRuns[character] = _pollRuns.GetValueOrDefault(character) + 1;
            _router.RunUpdateSelfBuffStats(unit, onLine);
            _router.RunUpdateSelfBuffActions(unit, onLine);
        }
    }

    // DIAGNOSTICS (batch 256): the polls each character's units ran, the measure of the unit poll cadence (the headless match prints it).
    private readonly Dictionary<string, long> _pollRuns = new(StringComparer.Ordinal);

    /// <summary>DIAGNOSTICS: the self-buff polls the bound units ran, by character.</summary>
    public IReadOnlyDictionary<string, long> PollRuns => _pollRuns;

    /// <summary>
    /// <c>BBSpellCast</c> of a spell slot by a non-champion unit (batch 174: CharScriptWorm's <c>CharOnPreDamage</c> casts its
    /// slot 3, <c>WrathoftheAncients</c>, 0130-0148, and its ActionTimer/ActionTimer2/SweepTimer buffs cast slots 3 and 0). A unit has
    /// no extra-slot spellbook, so only <c>SpellSlots</c> children exist; the spell is the character's own
    /// <c>Data*Spell{n+1}</c> (or the live name a <c>BBSetSpell</c> left), resolved through the same
    /// <see cref="BbSpellHost.ChildSpellName"/> a hero's child uses.
    /// <para>
    /// The cast runs by the same <c>CastType</c> switch as a champion child (<see cref="BbChampionModule.CastChild"/>): 1 a homing
    /// missile that runs the child's <c>TargetExecute</c> on arrival, 0 the effect at the cast frame (immediately, since these
    /// children carry no cast time of their own: <c>WrathoftheAncients</c> lists no <c>SpellData*CastTime</c>), 3 a line and 2 a chain
    /// stay unbound because no shipped unit character casts one. There is no cast packet for a non-hero: the client builds the
    /// cast from the unit's own spellbook, and the stock client draws a monster's cast from the missile and effect packets alone
    /// (Reconstruction: the hand-written Worm AI sent a 0x0D for each of its casts; a unit's cast answer has no reader, so this
    /// does not send one).
    /// </para>
    /// <para>
    /// <c>OverrideCoolDownCheck</c> false answers the slot's cooldown through <see cref="Entity.ScriptSlotReadyAt"/> like a hero's
    /// extra slot (the unit's slot records are the character's, so this is the module's own book, not a spellbook one) and starts it
    /// from the child's own <c>Cooldown</c>. <c>CharOnSpellCast</c> and <c>BuffOnSpellCast</c> of the unit's own character script run
    /// for the child, as they do for a hero's extra-slot child.
    /// </para>
    /// </summary>
    private void CastChild(BbChildCast child)
    {
        var host = _host ?? throw new InvalidOperationException("BbUnitCharacterModule.Bind was not called");
        var system = host.Spells;
        var caster = child.Caster;
        // Batch 174 part 2: a non-champion caster's extra slot is the name its own BBSetSpell stored (PropelSpellCaster names Propel into
        // ExtraSlots 0 of the geyser placemarker, then casts it); ChildSpellName reads that live name and refuses an empty slot below.
        if (child.SlotType is not (BbSlotType.SpellSlots or BbSlotType.ExtraSlots))
            throw BbSpellHost.Unbound($"a unit character's BBSpellCast of an {child.SlotType} slot", "M3");
        var name = host.ChildSpellName(caster, child.SlotType, child.ExtraIndex)
            ?? throw new BbRuntimeException($"{caster.Name} has no spell {child.ExtraIndex + 1}");
        var script = _scripts.FindOrShipped(name) ?? throw new BbRuntimeException($"{name} has no shipped script");
        var data = _data.Spell(name, 5);
        var castType = BbChampionModule.CastType(data);
        var rank = child.Level ?? 1;
        var flags = BbSpellTargeting.Names(BbSpellTargeting.Flags(data));
        var slot = child.Slot;
        // OverrideCoolDownCheck=false: the cast is refused while the slot is cooling down and starts the cooldown when it goes ahead.
        if (!child.OverrideCooldownCheck && caster.ScriptSlotReadyAt.TryGetValue(slot, out var readyAt) &&
            readyAt > system.CurrentTime + 1e-9) return;
        // The character's own cast hooks hear the child (RunChildSpellCast is the DispatchGate's guard against re-entry).
        RouterOrThrow.RunChildSpellCast(caster, slot, name, script, rank, child.Target);
        var wait = child.FireWithoutCasting ? 0.0 : data.CastDelay;
        var launchFrom = child.CastPosition ?? caster.Position;
        // Batch 481b: the spell the cast names for the client's missile (a team's guardian shot); the script and its data stay with name.
        var shot = OdinGuardianShot.Spell(caster, name);
        SpellCastInfo Info(uint missileId, Position3 at, Position3 end, IReadOnlyList<SpellCastTarget> targets) =>
            BbChampionModule.Channelled(script, new(SpellSystem.ProtocolHash(shot), caster.NetId, rank, caster.NetId, missileId, at, end,
                targets, 0.0f, 0.0f, checked((byte)slot), Ground(system, launchFrom), 1.0f, (float)data.CastDelay, 0.0f,
                (float)data.TotalTime, 0.0f));
        void Charge()
        {
            if (child.OverrideCooldownCheck) return;
            var cooldown = caster.CooldownsDisabled ? 0.0 : Math.Max(0.0, data.Cooldowns[Math.Clamp(rank, 1, data.Cooldowns.Count) - 1]);
            if (cooldown > 0.0) caster.ScriptSlotReadyAt[slot] = system.CurrentTime + cooldown;
            else caster.ScriptSlotReadyAt.Remove(slot);
        }
        if (castType == 1)
        {
            if (child.Target is not { Alive: true } target) return;
            Charge();
            var at = Ground(system, target.Position);
            var missileId = system.World.Allocate(UnitType.Missile);
            var info = Info(missileId, at, at, [new SpellCastTarget(target.NetId, at, 0)]);
            var flight = new BbSpellProjectile(Forget, host, script, caster, slot, rank,
                new ChampionCastContext(data, rank, 0.0, 0.0), info, target.NetId, missileId, targetFlags: flags,
                atWindupEnd: () => RouterOrThrow.RunSelfExecute(script, caster, slot, rank, 0.0, at, at, target),
                castTime: wait, child: true, launchFrom: child.CastPosition,
                // Batch 481b: the guardian's shot leaves its crystal bone (OdinGuardianShot); every other unit's missile starts on the ground.
                launchHeight: OdinGuardianShot.LaunchHeight(name, data));
            _flights.Add(flight);
            system.EnqueueSharedProjectile(flight);
            return;
        }
        if (castType is not 0)
            throw BbSpellHost.Unbound($"a unit character's child cast of CastType {castType} ({name})", "M3");
        // CastType 0: no missile; the effect lands when the child's cast time has passed (at once when it has none).
        Charge();
        var type = BbChampionModule.Targeting(data);
        var unitTarget = type is BbTargetType.TTYPE_Target or BbTargetType.TTYPE_TargetOrLocation && child.Target is { Alive: true } chosen
            ? chosen : null;
        var aimPoint = Ground(system, unitTarget?.Position ?? child.Position);
        IReadOnlyList<Entity> Victims(SpellSystem sys) => type switch
        {
            BbTargetType.TTYPE_Self => [caster],
            BbTargetType.TTYPE_Target => unitTarget is { Alive: true } ? [unitTarget] : Array.Empty<Entity>(),
            BbTargetType.TTYPE_SelfAOE => BbSpellTargeting.Area(sys, caster, caster.Position, data.CastRadius, flags),
            BbTargetType.TTYPE_Area => BbSpellTargeting.Area(sys, caster, child.Position, data.CastRadius, flags),
            _ => Array.Empty<Entity>(),
        };
        void Land(SpellSystem sys)
        {
            RouterOrThrow.RunSelfExecute(script, caster, slot, rank, 0.0, aimPoint, aimPoint, unitTarget);
            foreach (var unit in Victims(sys))
                RouterOrThrow.RunTargetExecute(script, caster, slot, rank, 0.0, aimPoint, aimPoint, unit);
        }
        if (wait <= 1e-9)
        {
            Land(system);
            return;
        }
        var windup = new BbSpellProjectile(Forget, host, script, caster, slot, rank,
            new ChampionCastContext(data, rank, 0.0, 0.0), Info(0, aimPoint, aimPoint,
                unitTarget is not null ? [new SpellCastTarget(unitTarget.NetId, aimPoint, 0)] : Array.Empty<SpellCastTarget>()),
            0, 0, caster.Position, default, aimPoint, windupTargets: sys => Victims(sys),
            atWindupEnd: () => RouterOrThrow.RunSelfExecute(script, caster, slot, rank, 0.0, aimPoint, aimPoint, unitTarget),
            castTime: wait, child: true);
        _flights.Add(windup);
        system.EnqueueSharedProjectile(windup);
    }

    /// <summary>
    /// Batch 466: the unit casts the spell of its own slot <paramref name="slot"/> (the record's <c>Data*Spell{n+1}</c>) on <paramref name="target"/>,
    /// the cast a script's <c>BBSpellCast</c> of <c>SpellSlots</c> makes (<see cref="CastChild"/>), with the slot's cooldown checked and started
    /// (<c>OverrideCoolDownCheck</c> false). A capture point guardian's attack is its Spell1 (OdinNeutralGuardian.inibin).
    /// </summary>
    public void CastSpellSlot(Entity unit, int slot, Entity target) =>
        CastChild(new BbChildCast(unit, slot, target, target.Position, target.Position, new BbHookFrame("AI", "", unit),
            SlotType: BbSlotType.SpellSlots, OverrideCooldownCheck: false));

    /// <summary>The spell a unit's own slot casts (the character record's <c>Data*Spell{n+1}</c>; a check's pin and the
    /// name <see cref="CastChild"/> resolves). Null when the module is not bound or the slot holds no name.</summary>
    internal string? SlotSpellName(Entity unit, int slot) =>
        _host?.ChildSpellName(unit, BbSlotType.SpellSlots, slot);

    /// <summary>The unit's flight list, retired through the shared projectile queue (the champion and item modules' own pattern).</summary>
    private readonly List<BbSpellProjectile> _flights = new();

    internal void Forget(BbSpellProjectile flight) => _flights.Remove(flight);

    /// <summary>The point the projectile layer aims at, on the navigation grid.</summary>
    private static Position3 Ground(SpellSystem system, Position2 point) =>
        new(point.X, system.Movement.Navigation.HeightAt(point), point.Z);

    /// <summary>
    /// Adds the shipped <c>Spells/&lt;name&gt;.luaobj</c> as a live script buff of this module's runtime without claiming the unit
    /// as a character (batch 159 part 3). The team-size catch-up is that case: <c>Spells/ChampionChampionDelta.luaobj</c> is a
    /// champion's own <c>CharOnActivate</c> add and a hero whose champion has a character-script module already gets it there,
    /// while the ten hand-written champions have no script host at all — the same buff is added through this seam so one
    /// instance, its periodic and its <c>BuffOnUpdateStats</c> serve both. <c>BUFF_REPLACE_EXISTING</c> is the add type every
    /// shipped <c>CharOnActivate</c> uses, so a second call replaces rather than stacks.
    /// </summary>
    public BuffState? AddShippedBuff(Entity unit, string name, BuffType type, double duration)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Buffs?.Add(new BbBuffRequest(unit, unit, name, (int)BbBuffAddType.BUFF_REPLACE_EXISTING,
            (int)type, 1, duration, 0.0, false, new BbTable()));
    }

    /// <summary>
    /// A buff one of this module's units' scripts added left its holder (<c>OnBuffDeactivate</c>): Spells/
    /// TurretPreBonus.luaobj hands over to TurretBonus the moment its idle window runs out, the growth hand-off a
    /// turret's own script stands on (batch 159 part 1). The removal is routed by the host
    /// (<c>SpellSystem.OnUnitBuffRemoved</c>) the way a champion module's, the summoner module's and the item
    /// module's are; the runtime answers only for the buff instances this module created.
    /// </summary>
    public void OnBuffRemoved(Entity unit, BuffState buff) => _host?.Buffs.OnRemoved(unit, buff);
}
