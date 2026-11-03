using System;
using System.Collections.Generic;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;
using Amberline.Simulation.Stats;

namespace Amberline.Tests.Spells;

/// <summary>
/// A match-shaped home for the non-champion unit character scripts (batch 159 part 2), for the checks that build their
/// own world instead of a <c>MatchRuntime</c>: one <see cref="BbUnitCharacterModule"/> over a real
/// <see cref="BuffLifecycle"/> (so a script's <c>BBSpellBuffAdd</c> lands as an ordinary <see cref="BuffState"/> on the
/// unit, which is what a scheduler's create burst then wires) and quiet services everywhere else. Production composes
/// the same four pieces in <c>ProductionMatchComposition</c>; this is the offline twin of that seam.
/// </summary>
internal sealed class UnitCharacterRig
{
    private readonly BbUnitCharacterModule _module;

    private UnitCharacterRig(BbUnitCharacterModule module, List<BbScriptFailure> failures)
    {
        _module = module;
        Failures = failures;
    }

    /// <summary>Hook runs that raised and were skipped; empty in a healthy run.</summary>
    public List<BbScriptFailure> Failures { get; }

    public static UnitCharacterRig Create(WorldState world, NavigationGrid grid)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(grid);
        var queue = new QuietQueue();
        var sync = new ReplicationSyncCounter();
        var bounds = new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ);
        var stats = new HeroStatsService(GameData.Default, new ItemCatalog(GameData.Default.Paths),
            new QuietStatsHooks(), new QuietStatsTransport());
        var effects = new ProtocolSpellEffectSink(queue, sync, bounds,
            (x, z) => grid.HeightAt(new Position2(x, z)), world.Allocate, _ => { });
        var lifecycle = new BuffLifecycle(world, stats, new QuietBuffServices(effects, world, queue));
        var movement = new MovementSystem(world, grid);
        var rig = Attach(world, movement, new QuietCombat(), effects, new LifecycleBuffs(lifecycle));
        rig.Lifecycle = lifecycle;
        return rig;
    }

    /// <summary>
    /// The same seam over a match world a harness already built (batch 173): the module and the spell system that polls
    /// it, bound to the harness's own combat, effects and buff lifecycle, so the replay runs the units' shipped character
    /// scripts exactly where production does.
    /// </summary>
    public static UnitCharacterRig Attach(WorldState world, MovementSystem movement,
        ISpellCombatService combat, ISpellEffectSink effects, ISpellBuffService buffs,
        GameData? data = null, Action<BbScriptFailure>? failureSink = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(movement);
        ArgumentNullException.ThrowIfNull(combat);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(buffs);
        var spells = SpellHost(world, movement, combat, effects, buffs, data);
        var failures = new List<BbScriptFailure>();
        var module = new BbUnitCharacterModule(data ?? GameData.Default);
        module.Bind(spells, failureSink ?? failures.Add);
        return new UnitCharacterRig(module, failures) { Spells = spells };
    }

    /// <summary>
    /// The spell system a harness puts the unit character module on: the same boundaries the harness already built, with a
    /// required-but-empty champion dispatcher (one module of a champion no unit here is named after) and no summoner or item
    /// scripts. <c>SpellSystem.Tick</c> is what polls the bound units' <c>UpdateSelfBuffStats</c>/<c>Actions</c> samples.
    /// </summary>
    public static SpellSystem SpellHost(WorldState world, MovementSystem movement,
        ISpellCombatService combat, ISpellEffectSink effects, ISpellBuffService buffs, GameData? data = null) =>
        new(world, movement, combat, effects, buffs, new QuietSpellHooks(),
            new ChampionSpellDispatcher([new BbChampionModule("Orianna", data ?? GameData.Default)]),
            data ?? GameData.Default,
            // Batch 174 part 2: a fixed script roll (the neutral harness's own value). SpellSystem's default is an unseeded source, and the
            // scripted rolls of a unit's character script (the Worm's propel and wrath choices) made its checks differ from process to process.
            new UnitRigScriptRandom(0.5));

    /// <summary>The spell system the module is bound to; a harness ticks it to poll the bound units (the match's own spell update).</summary>
    public SpellSystem? Spells { get; private init; }

    /// <summary>The rig's own lifecycle in <see cref="Create"/> (over a match world the harness passes its own instead).</summary>
    public BuffLifecycle? Lifecycle { get; private set; }

    /// <summary>Production's <c>BindUnitCharacter</c> seam: binds one unit to its shipped character script.</summary>
    public bool Bind(Entity unit, string character) => _module.BindUnit(unit, character);

    /// <summary>One spell update of every bound unit (the cadence <c>SpellSystem</c> keeps in a match).</summary>
    public void Poll() => _module.Poll();

    public void AssertNoFailures(string context) =>
        TestAssert.Equal(0, Failures.Count,
            Failures.Count == 0 ? context : context + ": " + string.Join("; ",
                Failures.ConvertAll(failure => failure.ToString())));

    private sealed class QuietQueue : ICombatPacketQueue
    {
        public void Enqueue(PlaintextPacket packet, bool global = false) { }
        public void EnqueueLifecycle(uint victimNetId, uint killerNetId,
            Func<uint, PlaintextPacket> buildPacket) => buildPacket(killerNetId);
    }

    private sealed class LifecycleBuffs(BuffLifecycle lifecycle) : ISpellBuffService
    {
        public void ScriptStatusGained(Entity unit, bool wasHardControlled, bool wasRooted) => lifecycle.ScriptStatusGained(unit, wasHardControlled, wasRooted);
        public void ScriptStatusLost(Entity unit, bool couldMove, bool couldAttack) => lifecycle.ScriptStatusLost(unit, couldMove, couldAttack);
        public BuffState? AddOverlapping(Entity unit, string name, BuffType type, double duration, uint casterNetId,
            BuffPayload? payload = null, bool canMitigateDuration = true, int? maxStack = null) =>
            lifecycle.AddOverlappingBuff(unit, name, type, duration, casterNetId, payload, canMitigateDuration, maxStack);
        public BuffState? Add(Entity unit, string name, BuffType type, double duration, uint casterNetId,
            BuffPayload? payload = null, bool canMitigateDuration = true) =>
            lifecycle.AddBuff(unit, name, type, duration, casterNetId, payload, canMitigateDuration);
        public BuffState? AddStacking(Entity unit, string name, BuffType type, double duration,
            uint casterNetId, int maxStacks, BuffPayload perStackPayload,
            bool replaceExistingPayload = false, bool canMitigateDuration = true) =>
            lifecycle.AddStackingBuff(unit, name, type, duration, casterNetId, maxStacks,
                perStackPayload, replaceExistingPayload, canMitigateDuration: canMitigateDuration);
        public BuffState? AddContinuing(Entity unit, string name, BuffType type, double duration, uint casterNetId,
            int maxStacks, BuffPayload perStackPayload, bool canMitigateDuration = true) =>
            lifecycle.AddStackingBuff(unit, name, type, duration, casterNetId, maxStacks, perStackPayload,
                continueTimer: true, canMitigateDuration: canMitigateDuration);
        public bool Remove(Entity unit, int slot) => lifecycle.RemoveBuff(unit, slot);
        public bool Has(Entity unit, string name) => lifecycle.HasBuff(unit, name);
        public void Tick(Entity unit, double dt) => lifecycle.TickBuffs(unit, dt);
        public void RefreshMovementSpeed(Entity unit, bool push = true) => lifecycle.RefreshMovementSpeed(unit, push);
        // A stats sample's re-run swaps the payload of the live instance (BuffLifecycle.ReplaceScriptPayload) — the
        // path HPByPlayerLevel's own BuffOnUpdateStats takes every time it computes a new HPIncrease.
        public bool ReplaceScriptPayload(Entity unit, BuffState buff, BuffPayload perStackPayload) =>
            lifecycle.ReplaceScriptPayload(unit, buff, perStackPayload);
        public bool DropScriptStack(Entity unit, BuffState buff, BuffPayload perStackPayload, int count = 1) =>
            lifecycle.DropScriptStack(unit, buff, perStackPayload, count);
        public bool CanAdd(Entity unit, string name, Entity? caster, BuffType type, int freeableSlots = 0) =>
            lifecycle.CanAdd(unit, name, caster, type, freeableSlots);
        public bool RefusesAdd(Entity unit, BuffType type, uint casterNetId, int? maxStack = null) =>
            lifecycle.RefusesAdd(unit, type, casterNetId, maxStack);
    }

    private sealed class QuietBuffServices : IBuffServices
    {
        public QuietBuffServices(ISpellEffectSink effects, WorldState world, ICombatPacketQueue queue)
        {
            Effects = new BuffEffectService(effects,
                new ProtocolBuffVisionSink(world.Get, world.Allocate, queue),
                _ => world.Clock.SyncId());
        }

        public ICombatBuffHooks Combat { get; } = new QuietCombatBuffHooks();
        public ICastBuffHooks Casts { get; } = new QuietCastHooks();
        public IChampionBuffHooks Champions { get; } = new QuietChampionHooks();
        public IBuffEffectHooks Effects { get; }
        public IItemBuffHooks Items { get; } = new QuietItemHooks();

        private sealed class QuietCombatBuffHooks : ICombatBuffHooks
        {
            public bool IsPendingActionBlocked(Entity unit) => unit.Buffs.Any(buff => buff.Payload.ActionDisabled);
            public double HealingMultiplier(Entity unit) => 1.0;
            public void InterruptBasicAttack(Entity unit) { }
            public void ApplyPeriodicDamage(PeriodicDamageRequest request) { }
            public void Kill(Entity unit, uint killerNetId = 0) { }
        }

        private sealed class QuietCastHooks : ICastBuffHooks
        {
            public void CancelPendingCast(Entity hero) => hero.PendingCast = null;
        }

        private sealed class QuietChampionHooks : IChampionBuffHooks
        {
            public void OnBuffTick(Entity unit, BuffState buff, double dt) { }
            public void OnBuffRemoved(Entity unit, BuffState buff) { }
        }

        private sealed class QuietItemHooks : IItemBuffHooks
        {
            public bool HasItem(Entity unit, int itemId) => false;
            public void SyncItemSlow(Entity target) { }
        }
    }

    private sealed class QuietStatsHooks : IHeroStatsHooks
    {
        public void UpdateAttackTiming(Entity hero, double attackSpeedMod) =>
            Amberline.Simulation.Stats.HeroAttackTimings.ApplyRefresh(hero, GameData.Default);
        public IReadOnlyList<double> ManaCosts(Entity hero) =>
            GameData.Default.ManaCostsForRanks(hero.SpellRanks, hero.ChampionName);
        public bool IsPendingActionBlocked(Entity hero) => false;
        public bool IsSpellToggledOn(Entity hero, int spellSlot) => false;
        public void RemoveBuff(Entity hero, int slot, bool emitWire) { }
    }

    private sealed class QuietStatsTransport : IHeroStatsTransport
    {
        public void Push(HeroStatsReplicationSnapshot snapshot) { }
        public void PushItemTooltips(uint heroNetId, IReadOnlyList<ItemTooltipEntry> entries) { }
    }

    private sealed class QuietSpellHooks : IChampionSpellHooks
    {
        public void OnSpellCast(Entity hero, int slot, double now) { }
        public void CancelChannel(Entity hero, string reason) { }
        public void OnNamedSpellCast(Entity hero, int slot, string spellName, Entity? target) { }
    }

    private sealed class QuietCombat : ISpellCombatService
    {
        public bool IsTargetable(Entity target, Entity? observer) => true;
        public double HealingMultiplier(Entity target) => 1.0;
        public bool ApplyDamage(Entity source, Entity target, double amount, DamageType type,
            DamageSourceKind? sourceKind, double vampScale, bool isReflect,
            bool sourceIsPeriodic, uint? spellOwnerNetId) => true;
        public bool ApplyDamage(Entity source, Entity target, double amount, DamageType type,
            DamageSourceKind? sourceKind, DeathRecapOrigin recapOrigin, bool bypassSpellShield) => true;
        public bool ConsumeSpellShield(Entity source, Entity target, bool force) => false;
        public void InterruptBasicAttack(Entity attacker) { }
        public void Kill(Entity unit, uint killerNetId) { }
    }

    /// <summary>A fixed script roll for a unit rig (Batch 174 part 2: a deterministic replacement for SpellSystem's unseeded default).</summary>
    internal sealed class UnitRigScriptRandom(double value) : IRandomSource
    {
        public double NextDouble() => value;
    }
}
