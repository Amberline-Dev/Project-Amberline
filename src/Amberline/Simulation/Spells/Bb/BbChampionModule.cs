using System.Collections.Concurrent;
using System.Globalization;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// A champion run from its shipped BuildingBlocks scripts. It is an ordinary <see cref="IChampionSpellModule"/>: validation
/// and cast timing come from the champion's and spells' inibins (the same data the hand-written modules read), the effects
/// come from the spell scripts' <c>SelfExecute</c> / <c>TargetExecute</c> hooks run by the interpreter on the production
/// services, the spell's own gate from its <c>CanCast</c> hook, and the passive and buffs from the character script and the
/// buff scripts of the closure. M2 registers <c>BbChampionModule("Soraka")</c> in production (the ten hand-written modules
/// stay); the differential checks still construct <c>BbChampionModule("Annie")</c> next to the hand-written module.
/// See docs/scripting/building-blocks-engine.md.
/// </summary>
public sealed class BbChampionModule : IChampionSpellModule
{
    // A champion's closure is immutable data; every production match composition (and every check) builds the module, so
    // the shipped scripts, the shared system buffs among them, are read once per process and champion.
    private static readonly ConcurrentDictionary<string, BbScriptSet> Closures = new(StringComparer.Ordinal);

    private readonly GameData _data;
    private readonly BbScriptSet _scripts;
    private readonly string[] _spellNames;
    private readonly SlotSpell[] _defaults;
    private readonly ConcurrentDictionary<(string Name, int Slot), SlotSpell> _swapped = new();
    private readonly List<BbSpellProjectile> _flights = new();
    private readonly List<BbLineMissile> _lines = new();
    private BbSpellHost? _host;

    public BbChampionModule(string champion, GameData? data = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(champion);
        _data = data ?? GameData.Default;
        ChampionName = champion;
        _scripts = Closures.GetOrAdd(_data.Paths.Root + "|" + champion, _ =>
        {
            var closure = new BbScriptResolver(_data.Paths).Resolve(champion);
            if (closure.Diagnostics.Count > 0)
                throw new InvalidDataException($"{champion} closure has diagnostics: {closure.Diagnostics[0].Message}");
            return new BbScriptSet(closure, _data.Paths);
        });
        _spellNames = _data.Champion(champion).SpellNames.ToArray();
        // The same rank counts SpellSystem.BeginChampionCast reads: the retail spellbook limit of the slot (five ranks for Q, W and E, three
        // for R, or the character's Data*MaxLevels); a spell whose ManaCost keys are stored as booleans (Soraka's Infuse costs nothing) still
        // has its ranks.
        _defaults = _spellNames.Select((name, slot) => BuildSlotSpell(name, slot)).ToArray();
    }

    /// <summary>
    /// Everything the module derives from the spell in one slot, built once per spell name and slot. A slot holds the character's
    /// <c>Data*Spell1..4</c> until a script swaps it (<c>BBSetSpell</c>, an <c>OverrideSpells</c> character push): the live name is
    /// <c>Entity.ActiveSpellNames</c>, see <see cref="SpellName"/>.
    /// </summary>
    private sealed record SlotSpell(string Name, SpellData Data, HashSet<string> Flags, BbScript? Script, string? UnboundNative,
        bool CastsWindupChild);

    private SlotSpell BuildSlotSpell(string name, int slot)
    {
        var data = _data.Spell(name, _data.SpellMaxLevel(ChampionName, slot));
        var script = _scripts.Find(name);
        var unbound = script?.Walk().Where(item => item.Hook is "SelfExecute" or "TargetExecute" or "CanCast")
            .Select(item => item.Block.Function)
            .FirstOrDefault(function => BbNativeCatalog.OwnerOf(function) is not null && !BbNativeHandlers.IsBound(function));
        // W22: a spawn block the spawn family refuses (an Orianna ball, a unit with an AI script no service owns) is refused with the unbound natives.
        unbound ??= script?.Walk().Where(item => item.Hook is "SelfExecute" or "TargetExecute" or "CanCast")
            .Select(item => BbNativeHandlers.SpawnRefusal(item.Block)).FirstOrDefault(reason => reason is not null);
        // Reconstruction: an instacast spell whose script casts an extra-slot child (Spiral Blade) is the cast the player sees: the child carries the
        // windup and the animation, so the cast takes the hero's order and attack like a spell with a windup of its own. A child fired without casting
        // (Cripple's missile child of Lee Sin's second Tempest, Orianna's Q) has none: that cast stays instant and leaves the order alone (owner decision
        // of 23 September 2026 for Orianna: Q, W and E keep the move order).
        // Batch 427 (owner, 28 October 2026, finding 17): Fiddlesticks' Drain is an instacast whose TargetExecute, not SelfExecute, casts its channel
        // child DrainChannel (a 0.267 s windup, then the 5 s channel). The press kept the hero's walk, so the walk carried into the channel and its Move
        // check ended it at the cast frame, after which the idle acquisition swung at the foe. A channel child cast from a press therefore takes the
        // order too (Reconstruction: the stock rule for a target press is in no binary we hold). Only channel children count, so the Orianna cases stay.
        var castsWindupChild = data.IsInstant && script is not null && script.Walk().Any(item =>
            item.Block.Function == "BBSpellCast" && item.Block.Param("FireWithoutCasting") is not BbValue.Bool { Value: true } &&
            (item.Hook == "SelfExecute" || item.Hook == "TargetExecute" && IsChannelChild(item.Block)));
        return new SlotSpell(name, data, BbSpellTargeting.Names(BbSpellTargeting.Flags(data)), script, unbound, castsWindupChild);
    }

    /// <summary>Whether a cast block names an extra-slot child that is a script channel (its own ChannelDuration with a channel hook).</summary>
    private bool IsChannelChild(BbBlock block)
    {
        if (block.Param("SlotType") is not BbValue.Reference { Name: "ExtraSlots" }) return false;
        var index = (int)(block.Number("SlotNumber") ?? 0.0);
        if (!_data.Champion(ChampionName).Raw.TryGetValue(InibinReader.KeyHash($"Data*ExtraSpell{index + 1}"), out var value) ||
            value.Kind != InibinValueKind.String || _scripts.Find(value.AsString()) is not { } child) return false;
        return BbScriptChannels.DurationOf(child) > 0.0;
    }

    private readonly ConcurrentDictionary<BbScript, bool> _spawnsAtEnd = new();

    /// <summary>Whether a missile spell's <c>SpellOnMissileEnd</c> spawns a unit where the missile ends (Orianna's Izuna: the ball).</summary>
    private bool SpawnsUnitAtMissileEnd(BbScript script) => _spawnsAtEnd.GetOrAdd(script, item =>
        item.Walk().Any(entry => entry.Hook == "SpellOnMissileEnd" && entry.Block.Function == "BBSpawnMinion"));

    /// <summary>
    /// The nearest floor point to <paramref name="to"/> on the ray from <paramref name="from"/>: <paramref name="to"/> itself when it and its client
    /// quantisation are walkable, else the first walkable point stepping back 10 units at a time, else <paramref name="from"/>. The owner's landing policy
    /// for the Orianna ball: the algorithm of the hand-written Q, recorded in docs/maintenance/2026-09-24-orianna-native-retest.md.
    /// </summary>
    internal static Position2 FloorLanding(SpellSystem system, Position2 from, Position2 to)
    {
        var grid = system.Movement.Navigation;
        bool Walkable(Position2 point) => grid.IsWalkable(point) && grid.IsWalkable(ChampionSpellSupport.ClientPoint(system, point));
        if (Walkable(to)) return to;
        var length = ChampionSpellSupport.Distance(from, to);
        for (var remaining = length - 10.0; remaining > 0.0; remaining -= 10.0)
        {
            var candidate = from + new Position2((to.X - from.X) * remaining / length, (to.Z - from.Z) * remaining / length);
            if (Walkable(candidate)) return candidate;
        }
        return from;
    }

    /// <summary>The spell the hero's <paramref name="slot"/> holds now (the live name over the character's default).</summary>
    private SlotSpell Current(Entity hero, int slot)
    {
        var name = SpellName(hero, slot);
        var fallback = _defaults[slot];
        return string.Equals(name, fallback.Name, StringComparison.OrdinalIgnoreCase)
            ? fallback : _swapped.GetOrAdd((name.ToLowerInvariant(), slot), _ => BuildSlotSpell(name, slot));
    }

    /// <summary>
    /// NOT RETAIL: retail runs whatever its scripts call. This engine binds only part of the natives, so a slot whose cast hooks call
    /// an unbound native (or whose targeting type the module cannot run) is refused before mana or cooldown are spent, instead of
    /// charging the player for a cast that would raise half way. Batch 241 audit (<c>audit bb-coverage</c> plus a walk of every hook of
    /// the 84 shipped champion closures): the natives the closures call that the engine does not bind are <c>BBGetDamagingBuffName</c>
    /// and <c>BBSetUseSlotSpellCooldownTime</c>, both only in <c>Spells/OdinCaptureChannel.luaobj</c> and
    /// <c>Spells/OdinCaptureChannelBomb.luaobj</c> (Teemo's closure, outside the cast hooks), and no spawn block of a cast hook is refused,
    /// so the gate refuses no shipped champion cast today; it stays for a native a later script reaches. Binding those two is the
    /// champion lanes' work.
    /// </summary>
    private static string? Unsupported(SlotSpell spell, BbTargetType type) => spell.Script is null ? "has no script"
        : spell.UnboundNative is not null ? $"calls {spell.UnboundNative}, which the engine does not run yet"
        : type is BbTargetType.TTYPE_Target or BbTargetType.TTYPE_Cone or BbTargetType.TTYPE_Self or BbTargetType.TTYPE_SelfAOE
            or BbTargetType.TTYPE_Location or BbTargetType.TTYPE_Area or BbTargetType.TTYPE_Direction
            or BbTargetType.TTYPE_TargetOrLocation or DragDirection or OffsetAOE or OffsetLocation ? null
        : $"is a {type} cast, which the engine does not run yet";

    // The ttype_e values 8 to 10 (TTYPE_DragDirection, TTYPE_OffsetAOE, TTYPE_OffsetLocation) have no script constant: the client registers
    // TTYPE_Self to TTYPE_Direction only, but its spell targeting factory builds a SpellTargeting for 0-10 (SpellTargetingDragLine, ...OffsetAOE,
    // ...OffsetLine) and the spell inibins carry them (Rumble's Carpet Bomb 8, Orianna's Dissonance and Detonate commands 9).
    private const BbTargetType DragDirection = (BbTargetType)8;
    private const BbTargetType OffsetAOE = (BbTargetType)9;
    private const BbTargetType OffsetLocation = (BbTargetType)10;

    public string ChampionName { get; }

    /// <summary>The scripts of the champion's closure; the checks use it to compare against the shipped tables.</summary>
    public IReadOnlyList<string> ScriptNames => _scripts.Closure.ScriptNames.ToArray();

    /// <summary>
    /// Binds the module to a match: builds the host, buff runtime and hook router over its services and returns the sink
    /// the match's gameplay hooks forward buff ticks, pre-damage, deaths and level-ups to. Call once per match.
    /// </summary>
    public IBbHookSink Bind(SpellSystem system, Action<BbScriptFailure>? failureSink = null)
    {
        if (_host is not null) throw new InvalidOperationException("the module is already bound to a match");
        var host = new BbSpellHost(system, (hero, slot) => SpellName(hero, slot));
        host.ChildCaster = CastChild;
        var router = new BbHookRouter(host, _scripts);
        host.Router = router;
        router.FailureSink = failureSink;
        host.Buffs = new BbBuffRuntime(host, _scripts, router);
        _host = host;
        Router = router;
        return router;
    }

    internal BbHookRouter? Router { get; private set; }

    /// <summary>Whether <see cref="Bind"/> ran (a match binds a script-driven module when its first hero is created).</summary>
    public bool IsBound => _host is not null;

    /// <summary>Hook runs that raised and were skipped (production safety); empty in a healthy match.</summary>
    public IReadOnlyList<BbScriptFailure> ScriptFailures => Router?.Failures ?? Array.Empty<BbScriptFailure>();
    internal BbBuffRuntime? Buffs => _host?.Buffs;
    /// <summary>The channelled spells the module's scripts run (a check reads it).</summary>
    internal BbScriptChannels? Channels => _host?.Channels;

    /// <summary>CharOnActivate and SetVarsByLevel for a hero of this champion (done once, when the hero is created).</summary>
    public void ActivateHero(Entity hero) => Router!.ActivateHero(hero);

    /// <summary>The persistent <c>CharVars</c> table of a hero (the checks read StunDuration from it).</summary>
    public BbTable CharVars(Entity hero) => Router!.CharVars(hero);

    private BbSpellHost Host => _host ?? throw new InvalidOperationException("BbChampionModule.Bind was not called");

    // ------------------------------------------------------------------------------------------------------------
    // IChampionSpellModule: data
    // ------------------------------------------------------------------------------------------------------------

    public bool Handles(Entity hero) => string.Equals(hero.ChampionName, ChampionName, StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// The spell in the hero's slot now: the live name <c>BBSetSpell</c> (and the spellbook re-init of an <c>OverrideSpells</c> character push)
    /// left in <see cref="Entity.ActiveSpellNames"/>, else the character's <c>Data*Spell1..4</c>. The slot is a property of the spellbook, not
    /// of the spell: its rank limit and rank gates stay those of the character's slot.
    /// </summary>
    public string SpellName(Entity hero, int slot) =>
        (uint)slot < (uint)hero.ActiveSpellNames.Count && hero.ActiveSpellNames[slot] is { Length: > 0 } live ? live : _spellNames[slot];
    /// <summary>
    /// The normal child an instacast slot spell casts from its <c>SelfExecute</c> (Sivir's Spiral Blade, Blitzcrank's Rocket Grab: the
    /// parent is an instacast, the child's cast packet creates the SpellCaster with the child's windup), or null. This is what makes a
    /// press of the slot held like a non-instant spell while another windup runs (<see cref="SpellSystem.CreatesSpellCaster"/>, the
    /// windup gate of 18 October 2026). A child fired without casting has no windup of its own and does not count.
    /// </summary>
    public string? CasterChildSpell(Entity hero, int slot)
    {
        if ((uint)slot >= (uint)_defaults.Length) return null;
        var current = Current(hero, slot);
        if (!current.Data.IsInstant || current.Script is null) return null;
        foreach (var (hook, block) in current.Script.Walk())
        {
            if (hook != "SelfExecute" || block.Function != "BBSpellCast" || block.Param("SlotType") is not BbValue.Reference { Name: "ExtraSlots" } ||
                block.Param("FireWithoutCasting") is BbValue.Bool { Value: true }) continue;
            var index = (int)(block.Number("SlotNumber") ?? 0.0);
            var stored = SpellSystem.ExtraSpellSlotStart + index;
            if ((uint)stored < (uint)hero.ActiveSpellNames.Count && hero.ActiveSpellNames[stored] is { Length: > 0 } live) return live;
            return _data.Champion(hero.StatCharacterName).Raw.TryGetValue(InibinReader.KeyHash($"Data*ExtraSpell{index + 1}"), out var value) &&
                   value.Kind == InibinValueKind.String ? value.AsString() : null;
        }
        return null;
    }

    public int MaxRank(Entity hero, int slot) => _defaults[slot].Data.MaxRank;
    // The spellbook's level requirement: the table of the stock executable, or the character's own Data*SpellsUpLevels row (Udyr: R is a normal rank-five spell).
    public int RankGate(Entity hero, int slot, int rank) => _data.SpellRankGate(ChampionName, slot, rank);
    public bool OwnsCooldown(Entity hero, int slot) => false;
    public ChampionTickPhase TickPhase => ChampionTickPhase.AfterCooldowns;
    // CharOnLevelUpSpell (Ryze: the Overload cooldown buff on the Q rank, +75 mana per R rank); the rank is already spent.
    public void OnRankUp(SpellSystem system, Entity hero, int slot, int rank) => Router?.RunLevelUpSpell(hero, slot);
    // The character script's own updates (Soraka's Consecration aura; Vayne's Night Hunter stats hook); a champion whose script has none costs one
    // lookup each. The stats hook runs first, as retail rebuilds the stat accumulators before the per-update actions. The actions hook still polls a
    // corpse (BbHookRouter.RunUpdateSelfBuffActions: the spellbook tooltip values update every second including while dead); what stops while the
    // hero is dead is its ally-affecting passives and auras (owner decision 20 Oct 2026) - the scripts' own CO_IS_NOT_DEAD, and BBIncExp's
    // dead-owner refusal where a script has no such test. The corpse's item copies poll too (BbItemModule.Poll). Batch 246: both hooks
    // run on the hero's self-buff line (SpellSystem.SelfUpdateDue, BbSelfUpdate), not on every step.
    public void Tick(SpellSystem system, Entity hero, double dt)
    {
        var due = system.SelfUpdateDue(hero);
        Router?.RunUpdateSelfBuffStats(hero, due);
        Router?.RunUpdateSelfBuffActions(hero, due);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Validation
    // ------------------------------------------------------------------------------------------------------------

    public SpellValidation Validate(SpellSystem system, Entity hero, int slot, uint targetNetId, Position3 position, double now, Position3? end = null)
    {
        var current = Current(hero, slot);
        var name = current.Name;
        var resolved = system.World.Get(targetNetId);
        var rank = hero.SpellRanks[slot];
        if (rank < 1) return Failure($"{name} has no rank learned", SpellCastFailure.Rank);
        // A script-driven hero the match never bound (the composition binds the module when the first hero is created).
        if (_host is null) return Failure($"{name}: the script module is not bound to this match", SpellCastFailure.Disabled);
        var type = Targeting(hero, slot, current.Data);
        if (Unsupported(current, type) is { } reason) return Failure($"{name} {reason}", SpellCastFailure.Disabled);
        type = EffectiveKind(system, hero, current, type, resolved);
        if (hero.SpellCooldowns[slot] > 0.0)
            return Failure(string.Format(CultureInfo.InvariantCulture, "{0} is on cooldown ({1:0.0}s left)", name,
                hero.SpellCooldowns[slot]), SpellCastFailure.Cooldown);
        // Batch 340 (security review): a windup pays at its cast frame, so until then its slot shows no cooldown. The client sends no cast
        // of a slot whose own SpellCaster is still running: the client's cast-targeted routine returns before the 0xA2 when the spellbook's casting object
        // is not stopped and its slot is
        // the active slot. The server refuses such a request in the same way (Reconstruction: the server's own test is not shipped).
        if (PendingCharges(hero).Any(flight => flight.Slot == slot))
            return Failure($"{name} is still being cast", SpellCastFailure.Cooldown);

        var spell = current.Data;
        // A slot a script buff toggled on (SpellToggleSlot, the client's eToggleOn) casts for free: the client's cost routine spends no PAR for it, see BbHookRouter.IsSpellToggledOn.
        var toggled = Router is { } toggles && toggles.IsSpellToggledOn(hero, slot);
        var mana = toggled ? 0.0 : SpellSystem.SlotResourceCost(hero, slot, spell.ManaCosts[rank - 1]);
        // Batch 340 (security review): the mana the hero's windups will spend at their cast frames is spoken for, so two casts pressed
        // together never cost more than the hero has. Reconstruction: how the retail server held the deferred cost is not shipped.
        var reserved = PendingCharges(hero).Sum(flight => flight.ReservedMana);
        if (hero.Mana - reserved < mana)
            return Failure(string.Format(CultureInfo.InvariantCulture, "not enough mana for {0} (need {1:0.0}, have {2:0}, {3:0} held by a windup)",
                name, mana, hero.Mana, reserved), SpellCastFailure.Mana);

        // The unit-target kinds are the TTYPE_Target ones, by the slot's targeting now (BBSetTargetingType may have changed it).
        var targetsUnit = type == BbTargetType.TTYPE_Target;
        if (targetsUnit)
        {
            // The target's team and kind come from the spell's own flags (an enemy-only Q, an ally-only W, an E for both).
            if (resolved is null) return Failure($"{name} target is not alive", SpellCastFailure.Target);
            if (BbSpellTargeting.RejectTarget(hero, resolved, current.Flags) is { } rejected)
                return Failure($"{name} {rejected}", SpellCastFailure.Target);
        }

        // Batch 324: a cone fires when the client's own test would fire it (Cone.Fire: the cone's final target position over the
        // request's end point as the aim, then the cone's can-fire test from where the hero stands); farther away it walks to the start
        // (SpellSystem.PendingCastPoint). Reconstruction: the retail server's own check of a cone request is not in the exe; this runs the
        // client's targeter for it. An aim within 30 of the hero has no direction (the targeter refuses it); the request is let through as
        // before (the cone takes no unit, the script aims by its own facing), because the client never sends one. A caller with no end point
        // (a usability probe) tests nothing here.
        if (type == BbTargetType.TTYPE_Cone && end is { } aim &&
            Cone.Fire(spell, hero.Position, new Position2(aim.X, aim.Z)) is { CanFire: false })
            return Failure($"{name} target is out of range (cone)", SpellCastFailure.Range);
        // Self and self-area spells have no cast point to check: their CastRange is the effect radius (Starcall 660, Wish 25000), not a reach.
        // A Direction cast has no cast point to reach either: the client's targeter puts its aim at CastRange + FlatCastRangeMod - 1 along the
        // cursor (the location targeter's Target, targeting type 7), so it fires at once wherever the cursor
        // is. A Location spell is range-tested even when it is a CastType 3 line missile (batch 324: the location targeter's can-fire test,
        // tests CastRange + FlatCastRangeMod for every Location; the shipped Location line-missile slot spells, Enchanted Crystal Arrow, Trueshot
        // Barrage, Bandage Toss and Hawkshot, carry a CastRange of 6000 to 25000).
        // Batch 331: every targeter's can-fire test uses GetCastRange (the slot's override, the spell instance's own value, else SpellData CastRange[level]:
        // plus the caster's FlatCastRangeMod: the targeters' own tests (TargetOrLocation, Target, Location,
        // Area), so the range here carries the hero's FlatCastRangeMod.
        // DragLine (type 8) tests its drag start (the first point its update hands on) the same way: the
        // request's Position is that start, so a Carpet Bomb started beyond 1700 walks first. OffsetLine (type 10) has no shipped spell.
        // Batch 340: OffsetAOE (type 9, Orianna's Dissonance and Shockwave) has no reach either. The client's start-cast routine
        // sends a Self (0), SelfAOE (4) or OffsetAOE (9) cast at the press, through the client's cast-targeted routine
        // with the owner's position as both points, so no targeter runs; the OffsetAOE targeter's own can-fire test would
        // only compare the point its target stores, that same owner position, with the caster. The effect follows the offset unit
        // (BBSetSpellOffsetTarget: the ball), which the scripts read themselves.
        var range = type is BbTargetType.TTYPE_Cone or BbTargetType.TTYPE_Self or BbTargetType.TTYPE_SelfAOE or BbTargetType.TTYPE_Direction or
                OffsetLocation or OffsetAOE ? 0.0
            : (CastRange(hero, slot) ?? spell.CastRangeAt(rank)) + hero.FlatCastRangeMod;
        if (range > 0.0)
        {
            var check = targetsUnit ? resolved!.Position : new Position2(position.X, position.Z);
            var reach = targetsUnit ? SpellSystem.UnitCastReach(spell, range, hero, resolved!) : range;
            var dx = hero.Position.X - check.X;
            var dz = hero.Position.Z - check.Z;
            if (dx * dx + dz * dz > reach * reach)
                return Failure(string.Format(CultureInfo.InvariantCulture, "{0} target is out of range ({1:0})", name, range),
                    SpellCastFailure.Range);
        }

        // The spell's own gate, after the data gates: Starcall refuses to fire with no enemy unit within 610. A target-class refusal, like the
        // flag checks above: the client's can-use check (the bot scripts' CanUseSpell(slot) == 0, SpellSystem.CanUseSpell) runs no script, so a
        // bot whose Starcall slot is learned and ready still draws it, and its ServerCastSpell fails here (the task ends without a cast).
        if (Router is { } router && current.Script is { } script && !router.RunCanCast(script, hero, slot, rank, mana))
            return Failure($"{name} has no valid target (script CanCast)", SpellCastFailure.Target);
        return SpellValidation.Success;
    }

    /// <summary>
    /// The spell's own CanCast gate, read for the replicated castable bit without a cast request (finding 32, 24 October 2026):
    /// Starcall refuses with no enemy unit within 610, and the client greys a slot whose bit is clear. A slot the module does not run,
    /// or whose script has no CanCast hook, is open.
    /// </summary>
    public bool ScriptCastGateOpen(Entity hero, int slot)
    {
        var rank = hero.SpellRanks[slot];
        if (rank < 1 || _host is null || Router is not { } router) return true;
        return Current(hero, slot).Script is not { } script || router.RunCanCast(script, hero, slot, rank, 0.0);
    }

    private static SpellValidation Failure(string reason, SpellCastFailure failure) => new(false, reason, failure);

    /// <summary>
    /// The reach the slot's spell script asks for this cast (<c>AdjustCastInfo</c>: Buster Shot's 600 plus 90 per level while Draw a Bead
    /// stands), or null for the spell's own. The cast gate and the walk into range both read it.
    /// </summary>
    public double? CastRange(Entity hero, int slot)
    {
        if (Router is not { } router || hero.SpellRanks[slot] < 1) return null;
        return Current(hero, slot).Script is { } script ? router.RunAdjustCastInfo(script, hero, slot, hero.SpellRanks[slot]) : null;
    }

    /// <summary>A ground-aimed slot spell that is itself a line missile (CastType 3: Lee Sin's Sonic Wave, Javelin Toss, Trueshot Barrage).</summary>
    private static bool IsLineSkillshot(SpellData spell, BbTargetType type) =>
        type is BbTargetType.TTYPE_Location or BbTargetType.TTYPE_Direction or OffsetLocation && CastType(spell) == 3;

    /// <summary>
    /// The kind a cast runs as. <c>TTYPE_TargetOrLocation</c> (Riven's Feint, Orianna's fast commands, the basic attacks of Janna, Karma, Leblanc and
    /// Morgana) is decided by the request: the targeter's Target keeps the clicked unit only when
    /// the spell data's valid-target test accepts it (its flags, and sight unless the targeting ignores it) and always stores the cursor position, and
    /// <c>CanFireSpell</c> measures the range to the unit when there is one, else to the position. So a request whose unit this server also admits
    /// is a unit-target cast and every other is a ground cast at the position. Every other kind is its own.
    /// </summary>
    private static BbTargetType EffectiveKind(SpellSystem system, Entity hero, SlotSpell spell, BbTargetType type, Entity? target) =>
        type != BbTargetType.TTYPE_TargetOrLocation ? type
        : AdmitsUnit(system, hero, spell, target) ? BbTargetType.TTYPE_Target : BbTargetType.TTYPE_Location;

    private static bool AdmitsUnit(SpellSystem system, Entity hero, SlotSpell spell, Entity? target) =>
        target is { Alive: true } unit && BbSpellTargeting.RejectTarget(hero, unit, spell.Flags) is null &&
        (!WorldState.IsEnemy(hero, unit) || system.CanSeeTarget(hero, unit));

    /// <summary>
    /// The targeting type of the hero's slot: what <c>BBSetTargetingType</c> set (the spell instance's getter answers the override
    /// first), else the spell's own (<see cref="Targeting(SpellData)"/>).
    /// </summary>
    private static BbTargetType Targeting(Entity hero, int slot, SpellData spell) =>
        hero.SpellTargetingOverrides.TryGetValue(slot, out var overridden) ? (BbTargetType)(uint)overridden : Targeting(spell);

    /// <summary>
    /// The spell's targeting type from its inibin (TTYPE_*); 1 and the 0/1 boolean packing both read as Target. The loader reads
    /// <c>TargettingType</c> through the int getter with default 1, so a spell without the key is a unit-target spell (the same default
    /// <see cref="SpellData.TargetsUnit"/> applies). No champion slot spell lacks the key; the six records that do are not slots.
    /// </summary>
    internal static BbTargetType Targeting(SpellData spell)
    {
        if (!spell.Raw.TryGetValue(InibinReader.KeyHash("SpellData*TargettingType"), out var value)) return BbTargetType.TTYPE_Target;
        // The client's int getter: a string reads as atoi of it (Twisted Fate's Destiny: "4CastRangeUseMapScaling=1" is 4).
        return (BbTargetType)(uint)value.AsClientInt();
    }

    /// <summary>
    /// SpellData CastType (0 instant effect, 1 missile, 2 chain missile, 3 line missile). The client creates a flying obj_SpellMissile
    /// only for 1, 2 and 3 (the cast frame's switch on the cast type; case 0 runs the effect at once), so a
    /// MissileSpeed on a CastType 0 spell (Imbue 1200, Rune Prison 500, Divine Blessing 20) is unused data.
    /// </summary>
    internal static int CastType(SpellData spell) => RawCount(spell, "SpellData*CastType");

    /// <summary>
    /// A small whole number of a spell inibin as the client reads it: the 0/1 values live in the boolean section, some in the
    /// string one (the client's getter converts), the rest are numbers.
    /// </summary>
    internal static int RawCount(SpellData spell, string key)
    {
        if (!spell.Raw.TryGetValue(InibinReader.KeyHash(key), out var value)) return 0;
        // The client's int getter (InibinValue.AsClientInt): a string is atoi of it, "4CastRangeUseMapScaling=1" included.
        return value.Kind is InibinValueKind.ByteVector or InibinValueKind.FloatVector ? 0 : value.AsClientInt();
    }

    // ------------------------------------------------------------------------------------------------------------
    // Execution
    // ------------------------------------------------------------------------------------------------------------

    public bool Execute(SpellSystem system, Entity hero, SpellCastRequest request, double now)
    {
        var host = Host;
        var slot = request.Slot;
        var rank = hero.SpellRanks[slot];
        var current = Current(hero, slot);
        var name = current.Name;
        var spell = current.Data;
        var script = current.Script!;
        var type = EffectiveKind(system, hero, current, Targeting(hero, slot, spell), system.World.Get(request.TargetNetId));
        // A unit-target cast turns the caster toward the unit it was cast at: the stock client does it when the cast's
        // animation starts (the client's animation routine; the same block is in the typed
        // client): NeedTargetSelect (a targeting type that names a unit),
        // the target is not the caster, its squared distance is above 4.0 and the unit's character data
        // has ShouldFaceTarget (default true). The server keeps its own facing in
        // step because the shipped scripts read it: YorickRavenous's TargetExecute spawns the Ravenous Ghoul at
        // BBGetPointByUnitFacingOffset(Owner, gap + 250), i.e. along the caster's facing, so a cast that did not turn
        // him spawned the ghoul behind him instead of past the foe (owner finding 34, 24 October 2026). No 0x53 is
        // sent: every client runs PlayAnimation for the cast it receives and turns the unit itself.
        if (type == BbTargetType.TTYPE_Target && system.World.Get(request.TargetNetId) is { } aimed &&
            aimed.NetId != hero.NetId &&
            DistanceSquared(hero.Position, aimed.Position) > FacingTurnMinimumSquared &&
            system.Data.ShouldFaceTarget(hero.UnitType == UnitType.Hero ? hero.ChampionName : hero.Name))
            Geometry.FaceTowards(hero, aimed.Position);
        // An instacast spell runs its script at once and does not take the hero's order (E); a windup spell does, and so does an
        // instacast one whose script casts a windup child (Sivir's Q).
        // Batch 575 (owner question 2, Orianna's Q, W and E in a swing's windup): an instacast leaves the swing alone, so it lands. The client's
        // own cast handler has that rule: the client's cast routine asks the current SpellCaster whether it is an auto attack. For
        // one, it hands an instacast to the instant-cast routine and returns: no stop of the swing, no new SpellCaster.
        // Only a cast that is not an instacast reaches the stop of the current caster. Reconstruction: the retail server's own
        // cast handler is not in the client; the shared instant-cast routine touches no SpellCaster either.
        var takesOrder = !spell.IsInstant || current.CastsWindupChild;
        var attacking = hero.AttackTarget is not null;
        // CharOnSpellCast and BuffOnSpellCast read the unit a unit-target cast is aimed at as Target (Lee Sin's character script roots the
        // target of Dragon's Rage for its windup); the cast hook runs inside BeginChampionCast, which carries no target.
        host.Router.CastTarget = type == BbTargetType.TTYPE_Target ? system.World.Get(request.TargetNetId) : null;
        host.Router.CastPoint = request.Position;
        ChampionCastContext cast;
        try
        {
            // A windup spell pays at its cast frame (BbSpellProjectile -> SpellSystem.ChargeChampionCast), an instacast here.
            cast = system.BeginChampionCast(hero, slot, name, rank, now, interruptOrder: takesOrder,
                free: Router is { } toggles && toggles.IsSpellToggledOn(hero, slot), atCastFrame: !spell.IsInstant);
        }
        finally { host.Router.CastTarget = null; host.Router.CastPoint = null; }
        // AdjustCooldown: the spell script may set the whole cooldown of this cast (the toggles' 0 or 0.5 s on the press that turns them on).
        if (Router is { } adjuster && adjuster.RunAdjustCooldown(script, hero, slot, rank) is { } adjusted)
        {
            var cooldown = hero.CooldownsDisabled ? 0.0 : adjusted;
            if (!cast.AtCastFrame) hero.SpellCooldowns[slot] = cooldown;
            cast = cast with { Cooldown = cooldown };
        }
        // Reconstruction (16 October 2026 playtest): the cast animation has to override the attack animation that is playing: the client only yields to a cast whose controller
        // starts (a windup cast of the champion's own slots), so the swing is stopped before the cast reaches it, as the cast-approach
        // path does for a hero that has to walk into range (SpellSystem.ApproachCastPoint).
        if (takesOrder && attacking && system.Effects.IsReferenceReady(hero)) system.Effects.SendStopAttack(hero);
        // The cast's stop is a one-point path, installed on receipt (the client's follow-path routine): seated on its anchor at once.
        if (takesOrder && system.Effects.IsReferenceReady(hero))
            system.Effects.SendWaypoints(hero, system.Movement.InstallStop(hero), system.World.Clock.SyncId());
        // No 0x8A here (batch 168): the cast's own cooldown travels in the cast answer below and the client starts it
        // itself, at the cast frame for a windup spell (the client's cast-frame routine ->
        // the cast routine -> the cost routine: the slot's remaining
        // time is raised to castInfo.cooldown when it is shorter) and at once for an instacast (the instant-cast routine ->
        // ApplyCostsOfSpell). A 0x8A sent here as well writes the same value a second time, cast time later, so the
        // client's record moves back up after it had already run down: with a cooldown that is not a whole number of
        // seconds (any cooldown reduction: 9 x 0.9 = 8.1 on Amumu's Tantrum, 3.5 x 0.9 = 3.15 on Ryze's Overload) the
        // HUD's whole-second readout crossed a second inside the 0.25 s windup and jumped back (owner finding 10/18,
        // 20 October 2026: "9 -> 8 -> 9"). Retail's server sends 0x8A only from LuaSpellScriptHelper::
        // SetSlotSpellCooldownTime, i.e. when a shipped script sets a cooldown (BbSpellHost, StartChildCooldown); the
        // engine's own cost application never does.
        switch (type)
        {
            // A CastType 1 spell flies its missile to a unit other than the caster; a CastType 0 one (Divine Blessing lists
            // MissileSpeed 20, Imbue 1200) has no flight and lands when its windup ends, and a self-cast has nowhere to fly.
            // A chain missile (CastType 2: Spell Flux) flies through the hops the server lists in the cast packet.
            case BbTargetType.TTYPE_Target when CastType(spell) == 2 && spell.MissileSpeed > 0.0 && !spell.IsInstant &&
                                                 request.TargetNetId != hero.NetId && BbChainParameters.Read(script) is { } chain:
                CastChain(system, host, hero, script, slot, rank, request, cast, spell, chain, current.Flags);
                break;
            case BbTargetType.TTYPE_Target when CastType(spell) == 1 && spell.MissileSpeed > 0.0 && !spell.IsInstant && request.TargetNetId != hero.NetId:
                CastHoming(system, host, hero, script, slot, rank, request, cast, current.Flags);
                break;
            case BbTargetType.TTYPE_Cone:
                CastCone(system, host, hero, script, slot, rank, request, cast);
                break;
            case BbTargetType.TTYPE_Location or BbTargetType.TTYPE_Direction or DragDirection or OffsetAOE or OffsetLocation:
                CastLocation(system, host, hero, script, slot, rank, request, cast, spell, current.Flags, type);
                break;
            case BbTargetType.TTYPE_Self:
                CastSelf(system, host, hero, script, slot, rank, cast, spell);
                break;
            case BbTargetType.TTYPE_Target:
                // No missile: the effect lands on the target when the windup ends (an instacast at once).
                CastTargeted(system, host, hero, script, slot, rank, request, cast, spell, current.Flags);
                break;
            case BbTargetType.TTYPE_SelfAOE:
                CastSelfArea(system, host, hero, script, slot, rank, request, cast, spell, current.Flags);
                break;
            // A ground area spell that is a CastType 3 line missile (Galio's Resolute Smite, Lux's Light Strike Kugel, Cassiopeia's Miasma): the
            // client flies the missile (DoCast_ClientAndServer creates one for CastType 1 to 3), and the damage and the area of SpellOnMissileEnd
            // land where it ends, so it takes the line path of a ground location spell (CastLocation) and has no windup victims.
            case BbTargetType.TTYPE_Area when CastType(spell) == 3 && spell.MissileSpeed > 0.0 && !spell.IsInstant:
                CastLocation(system, host, hero, script, slot, rank, request, cast, spell, current.Flags, BbTargetType.TTYPE_Location);
                break;
            case BbTargetType.TTYPE_Area:
                CastArea(system, host, hero, script, slot, rank, request, cast, spell, current.Flags);
                break;
            default:
                throw new InvalidOperationException($"{name}: {type} spells are not run by the engine yet");
        }
        // An instacast spell's buff publishes its own resource snapshot (CastSelf sends one when no buff did). A windup spell has spent nothing
        // yet: its cast frame pays and every client deducts the cost at its own cast frame (SpellSystem.ChargeChampionCast).
        return true;
    }

    private static Position3 Ground(SpellSystem system, Position2 point) =>
        new(point.X, system.Movement.Navigation.HeightAt(point), point.Z);

    /// <summary>
    /// The cast packet of a channelled spell (a script with a <c>ChannelDuration</c> and a channel hook). Client evidence (W25c,
    /// the client's spell-caster init): the SpellCaster ends at <c>mTimeSpellEnd = arrival + ExtraCastTime + DesignerTotalTime</c>
    /// and runs its cast frame at <c>arrival + ExtraCastTime + DesignerCastTime</c>; the channel itself (the loop clip, the HUD bar) starts at the
    /// cast frame and its length is the script's <c>ChannelDuration</c>, which the client reads from the shipped script. So the total time is the
    /// cast time plus the channel (the earlier <c>ChannelDuration</c> alone ended the client's SpellCaster, its bar and its winddown clip one cast
    /// time early). The force-casting bit (0x04) is read in one place, the client's cast routine: an extra-slot cast without it runs
    /// at once with no SpellCaster, so a channelling child (Drain, Infinite Duress) needs it; for slots 0 to 3 it changes nothing and it is kept.
    /// </summary>
    internal static SpellCastInfo Channelled(BbScript script, SpellCastInfo info) =>
        BbScriptChannels.DurationOf(script) is > 0.0 and var length
            ? info with { DesignerTotalTime = (float)(info.DesignerCastTime + length), Flags = (byte)(info.Flags | ChannellingFlag) } : info;

    /// <summary>The <c>isForceCastingOrChannelling</c> bit of <see cref="SpellCastInfo.Flags"/> (0x04).</summary>
    private const byte ChannellingFlag = 0x04;

    /// <summary>
    /// The <c>mShouldOverrideCastPosition</c> bit of <see cref="SpellCastInfo.Flags"/> (0x08). A cast made from a point other than the caster
    /// (a script's <c>BBSpellCast</c> with <c>OverrideCastPosition</c>: Orianna's Q and E leave from the ball, Brand's E from its target) says so
    /// on the wire, and the client then keeps the caster where it stands: the unit's cast routine
    /// snaps the caster to the packet's <c>CasterPos</c> only while the flag is clear, when that point is more than 400 units away
    /// (the squared distance of 400 units; the same gate in the typed client). The missile
    /// creation (the cast routine that makes missiles) reads it too: clear, a line or homing missile starts at the caster's
    /// named joint (the bone named by <c>SpellData*MissileBoneName</c>) instead of at <c>CasterPos</c>. So without the bit the client
    /// both teleports the caster to the ball and draws the ball's flight from the caster's hand. The flag reaches the wire in the cast-info byte
    /// that the cast-info reader splits into five booleans (bit 0 isAutoAttack, bit 2 forceCastingOrChannelling, bit 3 this one).
    /// </summary>
    internal const byte OverrideCastPositionFlag = 0x08;

    /// <summary>
    /// The <c>isAutoAttack</c> bit of <see cref="SpellCastInfo.Flags"/> (0x01), the same bit
    /// <see cref="BbHookRouter"/> sets on a script-overridden attack. A cast that carries it is the hero's own swing to the client: no HUD
    /// cast bar and, on a non-instant extra slot, a SpellCaster that plays the spell's own <c>AnimationName</c>
    /// (the client's cast routine and its spell-caster init).
    /// </summary>
    private const byte AutoAttackFlag = 0x01;

    /// <summary>
    /// The squared distance (4.0) above which the client's animation routine turns a
    /// unit-target caster toward its target. Two units in the same spot share a facing nothing
    /// can aim, so the client leaves it alone and so does the server.
    /// </summary>
    private const double FacingTurnMinimumSquared = 4.0;

    private static double DistanceSquared(Position2 first, Position2 second) =>
        (first.X - second.X) * (first.X - second.X) + (first.Z - second.Z) * (first.Z - second.Z);

    private void CastHoming(SpellSystem system, BbSpellHost host, Entity hero, BbScript script, int slot, int rank,
        SpellCastRequest request, ChampionCastContext cast, HashSet<string> flags)
    {
        var target = system.World.Get(request.TargetNetId);
        if (target is null || !target.Alive) return;
        var at = Ground(system, target.Position);
        var missileId = system.World.Allocate(UnitType.Missile);
        var castInfo = system.BuildChampionCastInfo(hero, cast, slot, at, [new SpellCastTarget(target.NetId, at, 0)], missileId, at);
        // The era-126 client starts a targeted missile from CastSpellAnswer: no second missile packet for the caster.
        if (system.Effects.IsReferenceReady(hero)) system.Effects.SendCast(hero, castInfo);
        // The target must stay a legal one for what the spell's flags admit (a friendly missile such as Imbue is not an enemy-only one).
        // SelfExecute runs at the cast frame, when the windup ends and before the missile leaves (see RunSelfExecute).
        var flight = new BbSpellProjectile(Forget, host, script, hero, slot, rank, cast, castInfo, target.NetId, missileId,
            targetFlags: flags, atWindupEnd: () => host.Router.RunSelfExecute(script, hero, slot, rank, cast.ManaCost, at, at));
        _flights.Add(flight);
        system.EnqueueSharedProjectile(flight);
    }

    private void CastChain(SpellSystem system, BbSpellHost host, Entity hero, BbScript script, int slot, int rank,
        SpellCastRequest request, ChampionCastContext cast, SpellData spell, BbChainParameters parameters, HashSet<string> flags)
    {
        var target = system.World.Get(request.TargetNetId);
        if (target is null || !target.Alive) return;
        var chain = parameters.Build(system, hero, target, spell, rank, flags);
        var at = Ground(system, target.Position);
        var missileId = system.World.Allocate(UnitType.Missile);
        // The cast names the first unit only (the client's chain missile asserts a single cast target); the hops follow in the
        // chain-sync packet, sent in the missile's name once the client's missile exists (BbChainSync: one windup after the cast).
        var castInfo = system.BuildChampionCastInfo(hero, cast, slot, at, [new SpellCastTarget(target.NetId, at, 0)], missileId, at);
        if (system.Effects.IsReferenceReady(hero)) system.Effects.SendCast(hero, castInfo);
        var hops = chain.Select(unit => unit.NetId).ToArray();
        var flight = new BbSpellProjectile(Forget, host, script, hero, slot, rank, cast, castInfo, target.NetId, missileId,
            targetFlags: flags, chain: hops,
            atWindupEnd: () => host.Router.RunSelfExecute(script, hero, slot, rank, cast.ManaCost, at, at));
        _flights.Add(flight);
        system.EnqueueSharedProjectile(flight);
        system.EnqueueSharedProjectile(new BbChainSync(hero, missileId, hops, cast.Data.CastTime));
    }

    /// <summary>
    /// A unit-target spell without a missile (Astral Blessing, Infuse). The cast packet names the target, SelfExecute runs at
    /// the cast, TargetExecute on the live target when the windup ends; losing the target in the windup costs nothing (it pays at its cast frame,
    /// batch 340), exactly as for the missile spells.
    /// </summary>
    private void CastTargeted(SpellSystem system, BbSpellHost host, Entity hero, BbScript script, int slot, int rank,
        SpellCastRequest request, ChampionCastContext cast, SpellData spell, HashSet<string> flags)
    {
        var target = system.World.Get(request.TargetNetId);
        if (target is null || !target.Alive) return;
        var at = Ground(system, target.Position);
        var castInfo = system.BuildChampionCastInfo(hero, cast, slot, at, [new SpellCastTarget(target.NetId, at, 0)], 0, at);
        castInfo = Channelled(script, castInfo);
        if (system.Effects.IsReferenceReady(hero)) system.Effects.SendCast(hero, castInfo);
        // The unit the cast was aimed at is the Target of a channel's hooks (Drain, Infinite Duress, Stand United); SelfExecute keeps the caster.
        if (spell.IsInstant)
        {
            host.Router.RunSelfExecute(script, hero, slot, rank, cast.ManaCost, at, at, channelTarget: target);
            host.Router.RunTargetExecute(script, hero, slot, rank, cast.ManaCost, at, at, target);
            return;
        }
        var flight = new BbSpellProjectile(Forget, host, script, hero, slot, rank, cast, castInfo, target.NetId, 0,
            targetFlags: flags,
            atWindupEnd: () => host.Router.RunSelfExecute(script, hero, slot, rank, cast.ManaCost, at, at, channelTarget: target));
        _flights.Add(flight);
        system.EnqueueSharedProjectile(flight);
    }

    /// <summary>
    /// A spell centred on its caster (Starcall, Wish, Monsoon). The cast packet lists the enemies within the radius; TargetExecute runs for each
    /// unit the spell's flags admit within its radius, measured from the caster when the windup ends (an instacast at once).
    /// </summary>
    private void CastSelfArea(SpellSystem system, BbSpellHost host, Entity hero, BbScript script, int slot, int rank,
        SpellCastRequest request, ChampionCastContext cast, SpellData spell, HashSet<string> flags)
    {
        var launch = Ground(system, hero.Position);
        var aim = launch;
        if (ReadsFacing(script) && AreaDirection(hero, request) is { } direction)
        {
            // Reconstruction (playtest 23 September 2026, Nidalee's Swipe): the area is self-centred but the script keeps the units in front of the
            // caster (CO_IS_TARGET_IN_FRONT_OF_ME reads her facing), so she turns to the aim first. The direction is the one the request's own
            // two points give, projected from her current position: a caster that has walked past a short endpoint does not face backward.
            var reach = spell.CastRangeAt(rank);
            hero.Facing = direction;
            aim = Ground(system, new Position2(hero.Position.X + direction.X * reach, hero.Position.Z + direction.Z * reach));
            if (system.Effects.IsReferenceReady(hero)) system.Effects.SendFaceDirection(hero, new Position3(direction.X, 0.0, direction.Z));
        }
        // The list is the enemies within the radius of the caster (the maintained cast-list rule, as CastArea): it is what reveals the
        // caster to the units it strikes (SpellRevealsChampion; Janna's Monsoon sets none, which the client reads as true).
        var castInfo = system.BuildChampionCastInfo(hero, cast, slot, aim, system.AreaCastTargets(hero, hero.Position, spell.CastRadius), 0, aim);
        castInfo = Channelled(script, castInfo);
        if (system.Effects.IsReferenceReady(hero)) system.Effects.SendCast(hero, castInfo);
        // SelfExecute runs at the cast frame (see BbHookRouter.RunSelfExecute): a windup spell runs it when the windup ends, right
        // before its targets (Taric's Command Power scans and damages from it and has no TargetExecute), an instacast at the cast.
        if (spell.IsInstant) host.Router.RunSelfExecute(script, hero, slot, rank, cast.ManaCost, launch, launch);
        IReadOnlyList<Entity> Victims(SpellSystem sys) => BbSpellTargeting.Area(sys, hero, hero.Position, spell.CastRadius, flags);
        if (spell.IsInstant)
        {
            foreach (var unit in Victims(system))
                host.Router.RunTargetExecute(script, hero, slot, rank, cast.ManaCost, launch, launch, unit);
            return;
        }
        var flight = new BbSpellProjectile(Forget, host, script, hero, slot, rank, cast, castInfo, 0, 0,
            hero.Position, default, launch, Victims,
            atWindupEnd: () => host.Router.RunSelfExecute(script, hero, slot, rank, cast.ManaCost, launch, launch));
        _flights.Add(flight);
        system.EnqueueSharedProjectile(flight);
    }

    /// <summary>
    /// A spell cast at a ground point whose effect is an area around it (Infernal Guardian: <c>TTYPE_Area</c>, a burst of <c>CastRadius</c>
    /// and a pet at the point). The cast packet lists the units the spell's flags admit within the radius of the clicked point
    /// (<see cref="SpellSystem.AreaCastTargets"/>, the maintained cast-list rule); <c>SelfExecute</c> runs at the cast frame, when the
    /// windup ends, and then <c>TargetExecute</c> once per unit within the radius of the cast point at that moment (an instacast: at
    /// the cast). <c>BBGetCastSpellTargetPos</c> answers the cast point in both hooks. Reconstruction: the stock server's area scan is
    /// server-only; the centre distance is the maintained area rule (see <see cref="BbSpellTargeting.Area"/>).
    /// <para>
    /// Owner decision, 24/25 September 2026 (playtest #4; module-layout.md "summon placement"): a spell that summons a pet projects a blocked
    /// aim to the nearest valid client-grid point with body clearance (<c>NavigationGrid.ClosestSummonPlacement</c>), and the burst and
    /// the landing effect follow the pet to that point, as the shipped script keeps all three on one <c>TargetPos</c>. The cast packet
    /// keeps the clicked point. An open, body-clear aim is unchanged.
    /// </para>
    /// </summary>
    private void CastArea(SpellSystem system, BbSpellHost host, Entity hero, BbScript script, int slot, int rank,
        SpellCastRequest request, ChampionCastContext cast, SpellData spell, HashSet<string> flags)
    {
        var clicked = new Position2(request.Position.X, request.Position.Z);
        var point = SummonFloor(system, script, clicked);
        var at = new Position3(point.X, request.Position.Y, point.Z);
        var castInfo = system.BuildChampionCastInfo(hero, cast, slot, new Position3(clicked.X, request.Position.Y, clicked.Z),
            system.AreaCastTargets(hero, clicked, spell.CastRadius), 0, new Position3(clicked.X, request.Position.Y, clicked.Z));
        castInfo = Channelled(script, castInfo);
        if (system.Effects.IsReferenceReady(hero)) system.Effects.SendCast(hero, castInfo);
        IReadOnlyList<Entity> Victims(SpellSystem sys) => BbSpellTargeting.Area(sys, hero, point, spell.CastRadius, flags);
        if (spell.IsInstant)
        {
            host.Router.RunSelfExecute(script, hero, slot, rank, cast.ManaCost, at, at);
            foreach (var unit in Victims(system))
                host.Router.RunTargetExecute(script, hero, slot, rank, cast.ManaCost, at, at, unit);
            return;
        }
        var flight = new BbSpellProjectile(Forget, host, script, hero, slot, rank, cast, castInfo, 0, 0, hero.Position, default, at,
            windupTargets: Victims, atWindupEnd: () => host.Router.RunSelfExecute(script, hero, slot, rank, cast.ManaCost, at, at));
        _flights.Add(flight);
        system.EnqueueSharedProjectile(flight);
    }

    /// <summary>
    /// The point a summoning area spell works at: the clicked point moved to the closest legal summon floor for the body of the pet or minion its
    /// <c>SelfExecute</c> spawns (<c>BBSpawnPet</c> and <c>BBSpawnMinion</c> name the character record in <c>Skin</c>), or the clicked point itself when the
    /// spell summons nothing. Reconstruction: Bushwhack's trap is the one minion spawn of the roster; the hand-written port refused a cast at a wall
    /// cell, the shipped script spawns at the cast point, and the summon floor keeps a trap out of the terrain without refusing the cast.
    /// A placemarker (<c>Placemarker=true</c>: Orianna's ball, Janna's TestCube) is no body that stands in the way, so the click is kept: the ball's own
    /// landing rule is the line's (<see cref="FloorLanding"/>, owner decision of 24 September 2026), not this one.
    /// </summary>
    private static Position2 SummonFloor(SpellSystem system, BbScript script, Position2 clicked)
    {
        var spawn = script.Walk().Where(item => item.Hook == "SelfExecute" && item.Block.Function is "BBSpawnPet" or "BBSpawnMinion")
            .Select(item => item.Block).FirstOrDefault();
        if (spawn?.Param("Placemarker") is BbValue.Bool { Value: true }) return clicked;
        return spawn?.Text("Skin") is { } skin
            ? system.Movement.Navigation.ClosestSummonPlacement(clicked, system.Data.Champion(skin).PathfindingRadius)
            : clicked;
    }

    /// <summary>
    /// A cast at a ground point (Spiral Blade): the hero turns to the point, the cast is announced and <c>SelfExecute</c> runs at the cast frame.
    /// Reconstruction (M3): the spell's whole effect is what that hook casts (the extra-slot child), so there is no windup flight of
    /// its own; the child starts when the parent's windup ends (<see cref="CastChild"/>). Two more kinds share the flow:
    /// <list type="bullet">
    /// <item>an <c>Area</c> cast (Annie's Summon: Tibbers, Nidalee's Bushwhack, Yorick's Decayed) is <see cref="CastArea"/>, which adds the
    /// summon floor and the cast-packet target list; the <c>Area</c> branch of <c>Units</c> below is not reached;</item>
    /// <item>a ground cast whose spell is itself a <c>CastType</c> 3 line missile (Lee Sin's Sonic Wave, Javelin Toss) announces the cast with the
    /// missile id and, after <c>SelfExecute</c>, starts the line of the slot's own spell (<see cref="BbLineMissile"/>), whose hits run the
    /// slot script's <c>TargetExecute</c>. A <c>Direction</c> cast is the same ground cast.</item>
    /// </list>
    /// </summary>
    private void CastLocation(SpellSystem system, BbSpellHost host, Entity hero, BbScript script, int slot, int rank,
        SpellCastRequest request, ChampionCastContext cast, SpellData spell, HashSet<string> flags, BbTargetType type)
    {
        var point = new Position2(request.Position.X, request.Position.Z);
        var at = Ground(system, point);
        // A drag cast (SpellTargetingDragLine) carries the start of the drag as its position and the end of it as its end position; every other
        // ground kind has one point. The announced end position and SelfExecute's end are the drag's end; GetCastSpellTargetPos stays the start.
        var end = type == DragDirection ? Ground(system, new Position2(request.EndPosition.X, request.EndPosition.Z)) : at;
        var facing = Geometry.FaceTowards(hero, point);
        var line = IsLineSkillshot(spell, type);
        var missileId = line ? system.World.Allocate(UnitType.Missile) : 0u;
        var castInfo = system.BuildChampionCastInfo(hero, cast, slot, at, Array.Empty<SpellCastTarget>(), missileId, end);
        castInfo = Channelled(script, castInfo);
        if (system.Effects.IsReferenceReady(hero))
        {
            system.Effects.SendFaceDirection(hero, new Position3(facing.X, 0.0, facing.Z));
            system.Effects.SendCast(hero, castInfo);
        }
        IReadOnlyList<Entity> Units(SpellSystem sys) => type == BbTargetType.TTYPE_Area
            ? BbSpellTargeting.Area(sys, hero, point, spell.CastRadius, flags) : Array.Empty<Entity>();
        void CastFrame()
        {
            host.Router.RunSelfExecute(script, hero, slot, rank, cast.ManaCost, at, end);
            if (!line) return;
            // The slot spell is the cast the client plays (its own SpellCaster, cast frame DesignerCastTime after the packet): the line completes
            // that cast frame before it can be retired, as the hand-written Sonic Wave and Mystic Shot did (see BbLineMissile.Tick).
            var missile = new BbLineMissile(Forget, host, script, spell.Name, hero, slot, rank, castInfo, 0.0, point, spell, flags,
                completesCast: true);
            _lines.Add(missile);
            system.EnqueueSharedProjectile(missile);
        }
        if (spell.IsInstant)
        {
            CastFrame();
            foreach (var unit in Units(system))
                host.Router.RunTargetExecute(script, hero, slot, rank, cast.ManaCost, at, at, unit);
            return;
        }
        // A ground spell with a windup of its own: SelfExecute at the cast frame (RunSelfExecute), then the units of an area.
        var flight = new BbSpellProjectile(Forget, host, script, hero, slot, rank, cast, castInfo, 0, 0, hero.Position, default, at,
            windupTargets: Units, atWindupEnd: CastFrame);
        _flights.Add(flight);
        system.EnqueueSharedProjectile(flight);
    }

    /// <summary>
    /// <c>BBSpellCast</c> of an extra-slot child. The child is announced like Ezreal's missile children (its own cast in slot
    /// 42 + n with the child's hash, a missile id and its designer times) and then runs by its <c>CastType</c>, the switch of the client's
    /// shared cast frame: 3 a line missile (it starts when the parent's cast time has
    /// passed), 1 a missile that homes on <c>TargetVar</c> and runs the child's <c>TargetExecute</c> on arrival, 0 no missile at all:
    /// the effect lands when the child's cast time has passed. Reconstruction: the child runs at the level the call forces
    /// (<c>OverrideForceLevel</c>) or else the parent's rank, its hooks run as the parent's slot so that damage and shield breaks are the
    /// parent's, and a child with <c>FireWithoutCasting</c> has no cast time of its own (an extra-slot child of CastType 0 or 1 otherwise waits
    /// its own <c>CastTime</c>, as Ezreal's Mystic Shot does with the child's 0.25 s). A CastType 0 child's targets follow its own targeting type:
    /// itself for Self, <c>TargetVar</c> for Target, the units around the caster or around the cast point for SelfAOE and Area, the units in the cone toward the point for a Cone, none for a
    /// location (Yorick's ghoul summons act in <c>SelfExecute</c>).
    /// </summary>
    private void CastChild(BbChildCast child)
    {
        var host = Host;
        var system = host.Spells;
        var hero = child.Caster;
        var name = host.ChildSpellName(hero, child.SlotType, child.ExtraIndex)
            ?? throw new BbRuntimeException($"{(hero.UnitType == UnitType.Hero ? hero.ChampionName : hero.Name)} has no " +
                                            $"{(child.SlotType == BbSlotType.SpellSlots ? "spell" : "extra spell")} {child.ExtraIndex + 1}");
        var script = _scripts.Find(name) ?? throw new BbRuntimeException($"{name} has no script in the {ChampionName} closure");
        // A spell a BuffOnPreAttack hook casts (Xin Zhao's Three Talon Strike) stands in for the swing: it reads the swing's roll as its HitResult.
        if (hero.UnitType == UnitType.Hero && string.Equals(child.Frame.Hook, "BuffOnPreAttack", StringComparison.Ordinal)) hero.SwingReplacedBy = name;
        var data = _data.Spell(name, 5);
        var castType = CastType(data);
        var parentSlot = child.Frame.SpellSlot;
        var rank = child.Level ?? child.Frame.SpellRank;
        var flags = BbSpellTargeting.Names(BbSpellTargeting.Flags(data));
        var extraSlot = checked((byte)child.Slot);
        // OverrideCoolDownCheck=false: the cast is refused while the slot is cooling down and starts the slot's cooldown when it goes ahead.
        if (!child.OverrideCooldownCheck && ChildOnCooldown(system, hero, child.Slot)) return;
        // CharOnSpellCast and BuffOnSpellCast also run for the cast of an extra-slot child a script makes (E20): Dr. Mundo's character script pays the
        // health of Infected Cleaver in the hook of "InfectedCleaverMissile", the child that the slot spell "InfectedCleaverMissileCast" casts, and that
        // child is flagged DoesntTriggerSpellCasts, which does not stop the character hook. It does stop the caster's BuffOnSpellCast for the child
        // (batch 324, BbHookRouter.RunChildSpellCast: Talon's Shadow Assault stealth survives its own ring of flagged children).
        if (hero.UnitType == UnitType.Hero && child.SlotType == BbSlotType.ExtraSlots)
            host.Router.RunChildSpellCast(hero, child.Slot, name, script, rank, child.Target);
        // Reconstruction (owner playtest 25 September 2026, #7 and #22): a child cast with a cast time of its own plays its clip (Mystic Shot's,
        // Spiral Blade's), which outlasts that cast time: the first order after the cast frame stops it (Entity.CastClipActive, SpellSystem.CancelOrder).
        // A child that carries the force-casting bit (Yorick's three summons) gets a SpellCaster on the client (its cast routine), which owns the
        // winddown as it does for a slot spell; the children of the playtest (Ezreal, Sivir, Blitzcrank: the script passes ForceCastingOrChannelling false) run
        // at once with no SpellCaster, so the clip is the server's to cut (Reconstruction; the F4 decision of 27 September for Yorick's W is kept).
        // A swing replacement also gets a SpellCaster (it carries the isAutoAttack bit): the client owns its clip and a server 0x2B would cut the
        // very animation the bit was set for. A child a BuffOnPreAttack hook casts stands in for the swing the script skipped (Xin Zhao's Three
        // Talon Strike, Renekton's Ruthless Predator): the client is told it is the hero's own ATTACK (the isAutoAttack flag, byte 0x01),
        // because two client rules need it (the client's cast routine and its spell-caster init): a cast answer with isAutoAttack clear
        // and forceCastingOrChannelling clear on an extra slot (42, 43, 44 ...) runs the cast routine straight from the packet
        // and builds NO SpellCaster, so the spell's own AnimationName (Spell1 / Spell2 / Spell3 here) is never played (owner finding 26); and
        // every spell-caster init builds for a non-auto-attack cast of the player's hero opens the HUD cast bar, an
        // isAutoAttack cast does not (owner finding 23: a bar on every Three Talon Strike attack).
        var swingReplacement = hero.UnitType == UnitType.Hero && string.Equals(child.Frame.Hook, "BuffOnPreAttack", StringComparison.Ordinal);
        if (hero.UnitType == UnitType.Hero && !child.FireWithoutCasting && !child.ForceCastingOrChannelling && !swingReplacement && data.CastDelay > 0.0)
            hero.CastClipActive = true;
        void Charge()
        {
            if (!child.OverrideCooldownCheck) StartChildCooldown(system, hero, child.Slot, parentSlot, data, rank);
        }
        // A child that channels (Fiddlesticks' DrainChannel, Warwick's InfiniteDuressChannel) announces its channel like a slot spell does; on an
        // extra slot the force-casting bit is also what makes the client build a SpellCaster at all (the client's cast routine).
        // The launch position of the cast packet and the origin of a missile: the caster, or the point the script's OverrideCastPosition names.
        var launchFrom = child.CastPosition ?? hero.Position;
        // A swing replacement keeps the child's own cast frame (the server lands its effect at CastDelay) but lives for the hero's
        // attack period, as the 0x0D's SpellCastInfo does (the attack-delay routine). The child's 0.175 s would end the
        // client's SpellCaster before the swing's own clip and hit at the end of the attack period.
        var attackTotal = swingReplacement ? Math.Max(hero.AttackPeriod, data.CastDelay) : data.TotalTime;
        SpellCastInfo Announce(uint missileId, Position3 at, Position3 end, IReadOnlyList<SpellCastTarget> targets)
        {
            // the reveal routine runs at the cast on the list the packet carries (the instant-cast call site serves the child
            // of an Instacast parent; the cast frame, the rest), for the child's own SpellRevealsChampion: the same routine
            // BuildChampionCastInfo runs for a slot spell. Ezreal's Arcane Shift bolt reveals him to its target at launch, after the blink.
            if (targets.Count > 0) system.RevealCaster(hero, data, targets);
            return Channelled(script, new(SpellSystem.ProtocolHash(name), hero.NetId, rank, hero.NetId, missileId, at, end, targets, 0.0f, 0.0f, extraSlot,
                Ground(system, launchFrom), 1.0f, (float)data.CastDelay, 0.0f, (float)attackTotal, 0.0f)
                {
                    Flags = (byte)((child.ForceCastingOrChannelling ? ChannellingFlag : (byte)0) |
                                   (child.OverrideCastPosition ? OverrideCastPositionFlag : (byte)0) |
                                   (swingReplacement ? AutoAttackFlag : (byte)0)),
                });
        }

        // Reconstruction (batch 393, owner findings 16, 58, 84): a child that launches a missile and is not an instacast reaches its caster's own client
        // as its 0x41 alone, with no cast answer. The cast answer would build a SpellCaster there when the spellbook is empty, and that caster opens
        // the HUD cast bar (batch 391 trace); the missile packet builds the object with no caster. A child of an instacast container keeps its cast
        // answer: the client completes the container's cast on the child's own SpellCaster (the 0x39 of the Ezreal Q and W and Blitzcrank Q checks),
        // which the missile alone would leave without one. The other viewers always keep the cast answer.
        var parentOfChild = parentSlot is >= 0 and < 4 ? Current(child.Frame.Caster ?? hero, parentSlot).Data : null;
        var missileOnlyForCaster = !data.IsInstant && !(parentOfChild is { IsInstant: true } && !child.FireWithoutCasting);
        // Batch 582 (owner finding 25, Gragas R's second barrel): an Instacast child that launches a missile reaches no view as a cast answer, only as
        // its missile's creation 0x41 (ProtocolSpellEffectSink.SendInstantMissileCast has the client evidence). The answer built a second missile on
        // the client whenever the parent's SpellCaster was still current there. The instacast container's child keeps its answer (above).
        var instantMissile = data.IsInstant && !(parentOfChild is { IsInstant: true } && !child.FireWithoutCasting);
        void SendChildCast(SpellCastInfo cast)
        {
            if (!system.Effects.IsReferenceReady(hero)) return;
            if (instantMissile) system.Effects.SendInstantMissileCast(hero, cast);
            else system.Effects.SendCast(hero, cast, missileOnlyForCaster: missileOnlyForCaster);
        }
        if (castType == 3)
        {
            var missileId = system.World.Allocate(UnitType.Missile);
            Charge();
            // LineMissileTrackUnits: the missile data names the unit (the client's single target, getNumTargets() <= 1), the cast starts toward
            // it and the client's line chases it by itself; the server's line follows the same unit (BbLineMissile.Track).
            var tracked = BbLineMissile.Tracks(data) && child.Target is { } chased ? chased : null;
            // Reconstruction (owner decision, 24 September 2026, "Q cannot finish inside terrain"): retail's landing for a line that ends in terrain is not recovered; a line whose end spawns a unit (Orianna's Izuna spawns the
            // ball) ends on floor. A point inside terrain, raw or in the client's quantised coordinates, backs off along the ray from the launch point in
            // 10-unit steps (FloorLanding); the announced aim, the missile and the unit all take that point. The scripts clamp the range and the
            // minimum themselves and would let the ball rest in a wall.
            var floorEnd = tracked is null && SpawnsUnitAtMissileEnd(script);
            var lineEnd = floorEnd ? FloorLanding(system, launchFrom, child.End) : child.End;
            var at = Ground(system, tracked?.Position ?? (floorEnd ? lineEnd : child.Position));
            var end = tracked is null ? Ground(system, lineEnd) : at;
            // Reconstruction: a unit the clients were never told about casts the line (Created false), and so does a placemarker (Janna's TestCube):
            // no cast is announced from it, whether or not its spawn has reached the clients (Created follows the observers' sight, so it is not a
            // rule of its own). Its missile is announced through the replication layer's temporary caster (MissileCasterProjection: the
            // TestCube template, the child's spell on extra slot 42 and the missile in one go, as the hand-written Howling Gale did), and
            // the packet names the point the line reaches at its full range, the aim the stock client accepted in the native playtests.
            MissileCasterProjection? projection = null;
            if (!hero.Created || hero.Placemarker)
            {
                var toward = ChampionSpellSupport.Direction(hero.Position, child.End);
                var range = data.LineRangeAt(rank);
                var far = Ground(system, hero.Position + new Position2(toward.X * range, toward.Z * range));
                at = end = far;
                projection = new MissileCasterProjection(hero.NetId, (child.Frame.Caster ?? hero).NetId, hero.Team,
                    Ground(system, hero.Position), new Position2(toward.X, toward.Z));
            }
            var info = Announce(missileId, at, end, tracked is null ? Array.Empty<SpellCastTarget>() : [new SpellCastTarget(tracked.NetId, at, 0)]);
            // Reconstruction (batch 393): the caster's own client gets this child's missile as the 0x41 alone (missileOnlyForCaster, above).
            if (projection is null) SendChildCast(info);
            // The child leaves when the cast does. A parent with a windup of its own runs SelfExecute (and so this BBSpellCast) at the cast
            // frame, when that windup is over; an instacast container (Spiral Blade) is the cast the player sees, so the child's line
            // starts after the container's cast time. A call that fires without casting (Janna's wave) has no cast time of its own to wait.
            // The container's cast is the child's cast: the line leaves when the CHILD's cast time has passed (Mystic Shot's 0.251 s, not the
            // container's 0.25 s; Spiral Blade, Rocket Grab and Essence Flux list the same figure in both), and the client's own cast frame is
            // completed with a FinishCast at that moment (below).
            var owner = child.Frame.Caster ?? hero;
            var parent = parentSlot is >= 0 and < 4 ? Current(owner, parentSlot).Data : null;
            var container = parent is { IsInstant: true } && !child.FireWithoutCasting;
            var delay = child.FireWithoutCasting ? 0.0 : parent is not null ? (parent.IsInstant ? data.CastDelay : 0.0) : data.CastTime;
            var line = new BbLineMissile(Forget, host, script, name, hero, parentSlot, rank, info, delay,
                tracked?.Position ?? lineEnd, data, flags, tracked, projection, completesCast: container, launchFrom: child.CastPosition);
            _lines.Add(line);
            system.EnqueueSharedProjectile(line);
            return;
        }

        var context = new ChampionCastContext(data, rank, 0.0, 0.0);
        // The child waits its effective cast delay: an Instacast child (VayneCondemnMissile lists 0.25 s and the Instacast flag) waits nothing,
        // a child with a cast time of its own (EzrealMysticShotMissile) waits it; the parent's windup came first.
        // Batch 428 (the owner's Maokai sapling: "the animation of throwing and the plant actually starting flying"): a child without ForceCastingOrChannelling runs
        // at once on the client (no SpellCaster), and a child of a parent with a windup of its own leaves at that parent's cast frame, as a line child does
        // (line 928). So the child's own CastDelay is not a second windup: MaokaiSapling2Boom's 0.25 s is not added to E's 0.375 s. Kept: a child of an instant
        // container (Mystic Shot's 0.25 s) and a ForceCastingOrChannelling child (Yorick's summons, whose windup the client shows). Reconstruction.
        var wait = child.FireWithoutCasting || (!child.ForceCastingOrChannelling && parentOfChild is { IsInstant: false }) ? 0.0 : data.CastDelay;
        if (castType == 2)
        {
            // A chain missile child (CastType 2): the cast names the first unit, the hops follow in the chain-sync packet, exactly as for a chain
            // slot spell (CastChain); the parameters are the child script's own ChainMissileParameters.
            if (child.Target is not { Alive: true } first) return;
            Charge();
            var parameters = BbChainParameters.Read(script)
                ?? throw new BbRuntimeException($"{name} is CastType 2 and has no ChainMissileParameters");
            var hops = parameters.Build(system, hero, first, data, rank, flags).Select(unit => unit.NetId).ToArray();
            var at = Ground(system, first.Position);
            var missileId = system.World.Allocate(UnitType.Missile);
            var info = Announce(missileId, at, at, [new SpellCastTarget(first.NetId, at, 0)]);
            if (instantMissile) SendChildCast(info);
            else if (system.Effects.IsReferenceReady(hero)) system.Effects.SendCast(hero, info);
            var flight = new BbSpellProjectile(Forget, host, script, hero, parentSlot, rank, context, info, first.NetId, missileId,
                targetFlags: flags, chain: hops, atWindupEnd: () => host.Router.RunSelfExecute(script, hero, parentSlot, rank, 0.0, at, at, first),
                castTime: wait, child: true);
            _flights.Add(flight);
            system.EnqueueSharedProjectile(flight);
            system.EnqueueSharedProjectile(new BbChainSync(hero, missileId, hops, wait));
            return;
        }
        if (castType is not (0 or 1))
            throw BbSpellHost.Unbound($"a child cast of CastType {castType} ({name})", "child casts");
        if (castType == 1)
        {
            // A homing missile needs its unit; without one there is nothing to fly to (the client's missile would have no target either).
            if (child.Target is not { Alive: true } target) return;
            Charge();
            var at = Ground(system, target.Position);
            var missileId = system.World.Allocate(UnitType.Missile);
            var info = Announce(missileId, at, at, [new SpellCastTarget(target.NetId, at, 0)]);
            // Reconstruction (batch 393): the homing missile child's cast answer leaves the caster's own client too (missileOnlyForCaster, above).
            SendChildCast(info);
            var flight = new BbSpellProjectile(Forget, host, script, hero, parentSlot, rank, context, info, target.NetId, missileId,
                targetFlags: flags, atWindupEnd: () => host.Router.RunSelfExecute(script, hero, parentSlot, rank, 0.0, at, at, target),
                castTime: wait, child: true, launchFrom: child.CastPosition);
            _flights.Add(flight);
            system.EnqueueSharedProjectile(flight);
            return;
        }

        // CastType 0: no missile; the effect lands when the child's cast time has passed.
        Charge();
        var type = Targeting(data);
        var unitTarget = type is BbTargetType.TTYPE_Target or BbTargetType.TTYPE_TargetOrLocation && child.Target is { Alive: true } chosen ? chosen : null;
        var aimPoint = unitTarget is not null ? Ground(system, unitTarget.Position) : Ground(system, child.Position);
        var landing = Announce(0, aimPoint, unitTarget is not null ? aimPoint : Ground(system, child.End),
            unitTarget is not null ? [new SpellCastTarget(unitTarget.NetId, aimPoint, 0)] : Array.Empty<SpellCastTarget>());
        // Reconstruction (batch 394, owner finding 84): a child with no missile and no channel of its own (Pantheon E's Heartseeker) reaches its caster's
        // own client as the 0x41 with MissileNetId 0 instead of the cast answer (ReplicationDelivery). The client runs the spell from that packet alone
        // (the cast routine's case 0) and builds no SpellCaster, so no second cast bar. A channel child keeps its answer.
        var casterOnly = missileOnlyForCaster && _data.SpellChannelDuration(name) <= 0.0;
        if (system.Effects.IsReferenceReady(hero)) system.Effects.SendCast(hero, landing, missileOnlyForCaster: casterOnly);
        IReadOnlyList<Entity> Victims(SpellSystem sys) => type switch
        {
            BbTargetType.TTYPE_Self => [hero],
            BbTargetType.TTYPE_Target => unitTarget is { Alive: true } ? [unitTarget] : Array.Empty<Entity>(),
            BbTargetType.TTYPE_SelfAOE => BbSpellTargeting.Area(sys, hero, hero.Position, data.CastRadius, flags),
            BbTargetType.TTYPE_Area => BbSpellTargeting.Area(sys, hero, child.Position, data.CastRadius, flags),
            // A cone child (Udyr's Phoenix Breath, cast from the third attack): the units in the cone from the caster toward the point, by the
            // spell's own cone data, as for a slot cone (CastCone).
            BbTargetType.TTYPE_Cone => Cone.Units(sys, data, hero.Position, new Position2(child.End.X - hero.Position.X, child.End.Z - hero.Position.Z), hero),
            _ => Array.Empty<Entity>(),
        };
        void Land(SpellSystem sys)
        {
            host.Router.RunSelfExecute(script, hero, parentSlot, rank, 0.0, aimPoint, aimPoint, unitTarget);
            foreach (var unit in Victims(sys))
                host.Router.RunTargetExecute(script, hero, parentSlot, rank, 0.0, aimPoint, aimPoint, unit);
        }
        if (wait <= 1e-9)
        {
            Land(system);
            return;
        }
        var windup = new BbSpellProjectile(Forget, host, script, hero, parentSlot, rank, context, landing, 0, 0, hero.Position, default,
            aimPoint, windupTargets: sys => Victims(sys), atWindupEnd: () => host.Router.RunSelfExecute(script, hero, parentSlot, rank, 0.0,
                aimPoint, aimPoint, unitTarget), castTime: wait, child: true);
        _flights.Add(windup);
        system.EnqueueSharedProjectile(windup);
    }

    /// <summary>
    /// The cooldown rule of <c>BBSpellCast</c> with <c>OverrideCoolDownCheck=false</c> (42 of the 323 shipped calls). The client's
    /// <c>BBSpellCast</c> body is a bare return, so the rule is a Reconstruction from the call sites: the cast goes
    /// through the ordinary cast path with the check on, i.e. it needs the slot to be off cooldown and then starts the slot's cooldown, and
    /// it never charges mana (41 of the 42 children list a cost of 0; the 42nd is the Wukong decoy clone's Double Attack, 40 mana, cast by a decoy minion, which the script does not make pay). The cooldown of a child is its own per-rank
    /// <c>Cooldown</c> (the loader's default of 10 s when the key is missing) times <c>1 + CooldownMod</c>, 0 with the cooldowns disabled, as
    /// for a champion slot. A hero's champion slot keeps it in its own list (and sends it); every other slot (an extra slot, or a monster's
    /// or a minion's spell slot, which nothing ticks down) in <see cref="Entity.ScriptSlotReadyAt"/>, and a hero's extra slot is sent too
    /// (see <see cref="StartChildCooldown"/>; the reconnect burst replays what is left). Reconstruction: retail's cooldown for a child that a champion's own
    /// spell hook casts (Gragas' and Heimerdinger's second casts, Miss Fortune's ricochet: their children carry the parent's cooldown) is never
    /// kept busy past the parent's own remaining cooldown, because our champion cooldown starts when the cast begins and the child's when the
    /// cast frame is reached, so the same value would refuse the recast of a parent that was pressed the moment it came off cooldown.
    /// </summary>
    private static bool ChildOnCooldown(SpellSystem system, Entity caster, int slot) =>
        caster.UnitType == UnitType.Hero && slot < caster.SpellCooldowns.Count
            ? caster.SpellCooldowns[slot] > 1e-9
            : caster.ScriptSlotReadyAt.TryGetValue(slot, out var readyAt) && readyAt > system.CurrentTime + 1e-9;

    private static void StartChildCooldown(SpellSystem system, Entity caster, int slot, int parentSlot, SpellData data, int rank)
    {
        var cooldown = caster.CooldownsDisabled ? 0.0
            : Math.Max(0.0, data.Cooldowns[Math.Clamp(rank, 1, data.Cooldowns.Count) - 1] * (1.0 + caster.CooldownMod));
        var hero = caster.UnitType == UnitType.Hero;
        if (hero && parentSlot != slot && (uint)parentSlot < (uint)caster.SpellCooldowns.Count && caster.SpellCooldowns[parentSlot] > 0.0)
            cooldown = Math.Min(cooldown, caster.SpellCooldowns[parentSlot]);
        if (hero && slot < caster.SpellCooldowns.Count)
        {
            caster.SpellCooldowns[slot] = cooldown;
            if (system.Effects.IsReferenceReady(caster)) system.Effects.SendCooldown(caster, slot, cooldown);
        }
        else
        {
            var had = caster.ScriptSlotReadyAt.ContainsKey(slot);
            if (cooldown > 0.0) caster.ScriptSlotReadyAt[slot] = system.CurrentTime + cooldown;
            else caster.ScriptSlotReadyAt.Remove(slot);
            // The wire, from the client's parse, as recorded in docs/maintenance/2026-10-19-w20d-script-cooldowns.md: 0x8A names the stored slot (0-51) in seven
            // bits and the summoner book in the eighth; the client's network handler hands it to the cooldown setter, which
            // writes the slot record's length and expiry (every unit's book builds all 52 records, so an extra slot with no spell is safe;
            // the spell-data lookup hard-asserts only at slot 52). Nothing but the local hero's four champion slots, two summoners and
            // its items is ever drawn, so a hero's extra slot gets the packet for the client's own record (what the stock server's
            // SetSlotSpellCooldownTime sends for every slot type) and a minion's or monster's slot gets none: nothing reads it, and the summoner
            // bit would dereference a book only heroes have.
            if (hero && slot < SpellSlotSeals.SlotCount && (cooldown > 0.0 || had) && system.Effects.IsReferenceReady(caster))
                system.Effects.SendCooldown(caster, slot, cooldown);
        }
    }

    /// <summary>Whether a spell's hooks test the units against the caster's facing (<c>CO_IS_TARGET_IN_FRONT_OF_ME</c>).</summary>
    private static bool ReadsFacing(BbScript script) =>
        script.Walk().Any(item => item.Block.Param("CompareOp") is BbValue.Reference { Name: "CO_IS_TARGET_IN_FRONT_OF_ME" });

    /// <summary>
    /// The unit direction a self-centred area cast is aimed along: from the request's point to its end point, else from the caster to the
    /// point; null when both are the caster's own position.
    /// </summary>
    private static Position2? AreaDirection(Entity hero, SpellCastRequest request)
    {
        var from = new Position2(request.Position.X, request.Position.Z);
        var to = new Position2(request.EndPosition.X, request.EndPosition.Z);
        var delta = new Position2(to.X - from.X, to.Z - from.Z);
        var length = Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
        if (length <= 1e-9)
        {
            delta = new Position2(from.X - hero.Position.X, from.Z - hero.Position.Z);
            length = Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
        }
        return length > 1e-9 ? new Position2(delta.X / length, delta.Z / length) : null;
    }

    private void CastCone(SpellSystem system, BbSpellHost host, Entity hero, BbScript script, int slot, int rank,
        SpellCastRequest request, ChampionCastContext cast)
    {
        var origin = hero.Position;
        var direction = new Position2(request.EndPosition.X - origin.X, request.EndPosition.Z - origin.Z);
        var length = Math.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
        var aim = request.EndPosition;
        var victims = Cone.Units(system, cast.Data, origin, direction, hero);
        // Reconstruction (batch 362, owner findings 2 and 111): the cone's SelfExecute aims by the caster's facing (Volley's and Rake's
        // BBGetPointByUnitFacingOffset read Owner), and the client turns him to the cone's direction (the face packet below), but the server
        // never did, so the arrows flew along whatever facing he had. He turns to the aim as CastLocation does for a ground point, and again at
        // this cast's own cast frame, so a second press during the windup does not aim this cone. An aim within 30 of him has no direction
        // (Cone.Fire) and keeps the facing.
        var turn = Cone.Fire(cast.Data, origin, new Position2(aim.X, aim.Z)) is not null;
        if (turn) Geometry.FaceTowards(hero, new Position2(aim.X, aim.Z));
        var castInfo = system.BuildChampionCastInfo(hero, cast, slot, aim,
            // The source cone packet carries target X/Z with a zero Y; only the caster launch point is sampled from nav height.
            victims.Select(unit => new SpellCastTarget(unit.NetId, new Position3(unit.Position.X, 0.0, unit.Position.Z), 0)).ToArray(),
            0, aim);
        castInfo = Channelled(script, castInfo);
        if (system.Effects.IsReferenceReady(hero))
        {
            if (length > 1e-9)
                system.Effects.SendFaceDirection(hero, new Position3(direction.X / length, 0.0, direction.Z / length));
            system.Effects.SendCast(hero, castInfo);
        }
        // SelfExecute runs at the cast frame, when the windup ends, before the units of the cone are taken (RunSelfExecute).
        var flight = new BbSpellProjectile(Forget, host, script, hero, slot, rank, cast, castInfo, 0, 0,
            origin, direction, aim, atWindupEnd: () =>
            {
                if (turn) Geometry.FaceTowards(hero, new Position2(aim.X, aim.Z));
                host.Router.RunSelfExecute(script, hero, slot, rank, cast.ManaCost, aim, aim);
            });
        _flights.Add(flight);
        system.EnqueueSharedProjectile(flight);
    }

    private void CastSelf(SpellSystem system, BbSpellHost host, Entity hero, BbScript script, int slot, int rank,
        ChampionCastContext cast, SpellData spell)
    {
        var launch = Ground(system, hero.Position);
        // The cast is announced first, like every other cast kind, and the script runs at the cast frame after it. A script that swaps its own slot
        // (BBSetSpell: Twisted Fate's Pick A Card, Garen's Judgment) sends a 0x35 from SelfExecute, and that must follow the 0xBD that names the
        // spell cast: the client applies an instacast's costs on receipt (the client's cast routine -> the instant-cast routine ->
        // the cost routine), which compares the spell the slot holds with the cast's own and starts the slot's cooldown from the cast only when they are the same spell.
        // Nothing in the packet depends on the script (the cost, the cooldown, AdjustCooldown's too, and the rank were fixed by BeginChampionCast).
        var info = system.BuildChampionCastInfo(hero, cast, slot, launch, Array.Empty<SpellCastTarget>(), 0, launch);
        info = Channelled(script, info);
        if (system.Effects.IsReferenceReady(hero)) system.Effects.SendCast(hero, info);
        if (!spell.IsInstant)
        {
            // A self spell with a windup: announced now, SelfExecute and the caster's TargetExecute at the cast frame (RunSelfExecute).
            var windup = new BbSpellProjectile(Forget, host, script, hero, slot, rank, cast, info, 0, 0, hero.Position, default, launch,
                windupTargets: _ => [hero], atWindupEnd: () => host.Router.RunSelfExecute(script, hero, slot, rank, cast.ManaCost, launch, launch));
            _flights.Add(windup);
            system.EnqueueSharedProjectile(windup);
            return;
        }
        var buffsBefore = hero.Buffs.Count;
        host.Router.RunSelfExecute(script, hero, slot, rank, cast.ManaCost, launch, launch);
        host.Router.RunTargetExecute(script, hero, slot, rank, cast.ManaCost, launch, launch, hero);
        // Publish the spent mana when no buff did (a full table can omit the script's buff). After the cast packet, which deducts the
        // cost from the client's own mana and makes the stat store forget it (patch126-replication-tables.md), so the figure is sent again.
        if (hero.Buffs.Count == buffsBefore && system.Effects.IsReferenceReady(hero)) system.Effects.SendStats(hero);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------------------------------------------------

    internal void Forget(BbSpellProjectile flight) => _flights.Remove(flight);

    /// <summary>The hero's windups whose cast frame has not spent their cost yet (batch 340, <see cref="SpellSystem.ChargeChampionCast"/>).</summary>
    private IEnumerable<BbSpellProjectile> PendingCharges(Entity hero) =>
        _flights.Where(flight => ReferenceEquals(flight.Caster, hero) && flight.ChargePending);
    internal void Forget(BbLineMissile line) => _lines.Remove(line);

    public void OnHeroDeath(SpellSystem system, Entity hero)
    {
        // A channel ends with its caster (ChannelingStopSource_Die).
        _host?.Channels.Stop(hero, BbChannelingStopCondition.ChannelingStopCondition_Cancel, BbChannelingStopSource.ChannelingStopSource_Die);
        // A cast that dies in its windup is cancelled; a launched missile or an elapsed effect is not.
        hero.CastClipActive = false;
        foreach (var flight in _flights.Where(item => ReferenceEquals(item.Caster, hero)).ToArray())
            flight.CancelBeforeLaunch(system);
        foreach (var line in _lines.Where(item => ReferenceEquals(item.Caster, hero)).ToArray())
            line.CancelBeforeLaunch(system);
    }

    public int RetireTargetedFlights(SpellSystem system, Entity hero, uint targetNetId, TargetedFlightRetirement reason)
    {
        // Batch 340: a windup pays at its cast frame (DoCast_ClientAndServer -> ApplyCostsOfSpell), so a cast whose target is lost before it
        // has spent nothing; the flight ends and the client is told the slot's cooldown and the mana (SpellSystem.RefundChampionCast, the one
        // Reconstruction left: which stop and packets the retail server sent). The shared queue has already removed the entry and destroyed a
        // launched missile.
        var refunded = 0;
        foreach (var flight in _flights.Where(item => ReferenceEquals(item.Caster, hero) && item.TargetNetId == targetNetId).ToArray())
            if (flight.RefundOnTargetLoss(system)) refunded++;
            // A launched missile the queue took out for an untargetable target is over (its target's death keeps it flying to the corpse).
            else if (reason == TargetedFlightRetirement.Untargetable) flight.EndRetired(system);
        return refunded;
    }

    public void OnBuffRemoved(SpellSystem system, Entity unit, BuffState buff) => _host?.Buffs.OnRemoved(unit, buff);

    /// <summary>
    /// Once per spell step, after the movement phase: the displacements the champion's scripts started (<c>BBMove</c>) that are over
    /// release the movement system and run their <c>BuffOnMove*</c> hooks (<see cref="BbScriptMoves.Tick"/>).
    /// </summary>
    public void TickSharedProjectilePhase(SpellSystem system, double dt)
    {
        _host?.Moves.Tick();
        _host?.Channels.Tick(dt);
    }
}
