using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.Ai;
using Amberline.Simulation.BehaviorTrees;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Bots;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Items;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Neutral;
using Amberline.Simulation.Pets;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;
using Amberline.Simulation.Stats;

namespace Amberline.Network.Sessions;

public sealed class ProductionSimulationSpeed
{
    public const double MinimumMultiplier = 0.1;
    public const double MaximumMultiplier = 10.0;

    public double Multiplier { get; private set; } = 1.0;
    public bool ClockResyncPending { get; set; }

    public static bool IsValid(double value) =>
        double.IsFinite(value) && value is >= MinimumMultiplier and <= MaximumMultiplier;

    public bool TrySet(double value)
    {
        if (!IsValid(value)) return false;
        Multiplier = value;
        return true;
    }
}

internal sealed class ProductionSimulationDriver : IMatchSimulationDriver
{
    private readonly WorldState _world;
    private readonly ReplicationDelivery _delivery;
    private readonly MovementSystem _movement;
    private readonly CombatService _combat;
    private readonly SpellSystem _spells;
    private readonly BuffLifecycle _buffs;
    private readonly NeutralAiService _neutral;
    private readonly JungleScheduler _jungle;
    private readonly ItemService _items;
    private readonly MinionAiSystem _minions;
    private readonly WaveScheduler _waves;
    private readonly TibbersAiSystem _tibbers;
    private readonly PetAiService _pets;
    private readonly ShacoBoxService _boxes;
    private readonly ShopService _shop;
    private readonly ItemPassiveService _itemPassives;
    private readonly HeroAttackOrderSystem _heroOrders;
    private readonly BotController _bots;
    private readonly bool _visibilityBaseline;
    private readonly bool _minionFidelity;
    private readonly FidelityTrace? _fidelityTrace;
    private readonly CollisionDiagnosticSink? _movementTrace;
    private MatchRuntime? _runtime;
    private double _movementAccumulator;
    private double _heroMovementAccumulator;
    private bool _attached;

    /// <summary>Applies the orders the dispatcher held back during an uncancellable spell windup.</summary>
    public Action? ApplyDeferredOrders { get; init; }

    /// <summary>Map 8's quest host (Dominion); null on Summoner's Rift and the Harrowing rift, which run no behaviour trees.</summary>
    public BehaviorTreeQuestHost? Quests { get; init; }

    /// <summary>Batch 674: a map's own server script, ticked once per step with the game time (Battle Training's; null elsewhere).</summary>
    public Action<double>? MapScript { get; init; }
    public PlaytestDiagnostics? PlaytestDiagnostics { get; init; }

    public ProductionSimulationDriver(WorldState world, ReplicationDelivery delivery,
        MovementSystem movement,
        CombatService combat, SpellSystem spells, BuffLifecycle buffs,
        NeutralAiService neutral, JungleScheduler jungle, ItemService items,
        MinionAiSystem minions, WaveScheduler waves, TibbersAiSystem tibbers,
        PetAiService pets, ShacoBoxService boxes,
        ShopService shop,
        ItemPassiveService itemPassives, HeroAttackOrderSystem heroOrders,
        BotController bots,
        FidelityTrace? fidelityTrace = null,
        bool visibilityBaseline = false,
        bool minionFidelity = false,
        CollisionDiagnosticSink? movementTrace = null)
    {
        _world = world;
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _movement = movement; _combat = combat; _spells = spells;
        _buffs = buffs; _neutral = neutral; _jungle = jungle; _items = items;
        _minions = minions; _waves = waves; _tibbers = tibbers;
        _pets = pets ?? throw new ArgumentNullException(nameof(pets));
        _boxes = boxes ?? throw new ArgumentNullException(nameof(boxes));
        _shop = shop ?? throw new ArgumentNullException(nameof(shop));
        _itemPassives = itemPassives ?? throw new ArgumentNullException(nameof(itemPassives));
        _heroOrders = heroOrders;
        _bots = bots ?? throw new ArgumentNullException(nameof(bots));
        _fidelityTrace = fidelityTrace;
        _movementTrace = movementTrace;
        _visibilityBaseline = visibilityBaseline;
        _minionFidelity = minionFidelity;
    }

    public void Attach(MatchRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!ReferenceEquals(runtime.World, _world)) throw new ArgumentException("world mismatch", nameof(runtime));
        if (_attached) throw new InvalidOperationException("simulation driver already attached");
        _runtime = runtime;
        _movement.HeroPathChanged = (hero, path) =>
        {
            var sync = _world.Clock.SyncId();
            _delivery.Enqueue(VisionPackets.WaypointList(hero.NetId, path, checked((uint)sync)));
            _fidelityTrace?.NoteOrder(hero.NetId, "waypoints", "hero-automatic", sync, path);
            _movementTrace?.NoteWaypointIntent(hero.NetId, "hero-automatic", sync, path);
        };
        // Batch 288: the stop of the server's forced-stop routine for any other unit, sent as that unit's stops are (a one-point path).
        _movement.ServerStopPublished = (unit, stop) =>
        {
            if (!unit.Created) return;
            var sync = _world.Clock.SyncId();
            _delivery.Enqueue(VisionPackets.WaypointList(unit.NetId, stop, checked((uint)sync)));
            _fidelityTrace?.NoteOrder(unit.NetId, "waypoint", "force-stop-server", sync, stop);
            _movementTrace?.NoteWaypointIntent(unit.NetId, "force-stop-server", sync, stop);
        };
        // The waves start first: their OnLevelInit on the shared level host is what the camp chunks need. The visibility baseline
        // has no waves, so it runs the level init on the host itself, with the economy timers stopped as the standalone scheduler does.
        if (!_visibilityBaseline)
            _waves.Start(_combat.CurrentTime,
                _minionFidelity ? new[] { "C" } : null);
        else
        {
            _waves.LevelScript.OnLevelInit(_combat.CurrentTime);
            _waves.LevelScript.StopNamedTimers();
        }
        _jungle.Start(_combat.CurrentTime);
        _attached = true;
    }

    public void Tick(double dt)
    {
        if (!_attached) throw new InvalidOperationException("simulation driver is not attached");
        if (!double.IsFinite(dt) || dt < 0.0)
            throw new ArgumentOutOfRangeException(nameof(dt));
        if (_runtime is null || !_runtime.Session.Started)
            return;
        _fidelityTrace?.BeginTick();
        if (_world.Clock.IsVirtual) _world.Clock.Advance(dt);
        _movement.AdvanceSimulationTime(dt);
        // Batch 474f: the Dominion opening window (the quests run and the release is still ahead) holds the champions in the movement layer.
        _movement.OpeningHoldWindow = Quests is not null && !_combat.GameOver && _combat.CurrentTime < OpeningCeremonyTiming.ReleaseSeconds;
        if (!_combat.GameOver && !_runtime.Session.Paused) _bots.Tick(dt);
        if (!_combat.GameOver) _heroOrders.Tick(dt);
        _heroMovementAccumulator += dt;
        while (_heroMovementAccumulator + 1e-12 >= MovementSystem.HeroTickSeconds)
        {
            _movement.TickHeroes();
            _heroMovementAccumulator -= MovementSystem.HeroTickSeconds;
        }
        _movementAccumulator += dt;
        while (_movementAccumulator + 1e-12 >= MovementSystem.FixedTickSeconds)
        {
            _movement.TickNonHeroes();
            _movementAccumulator -= MovementSystem.FixedTickSeconds;
        }
        if (!_combat.GameOver && !_visibilityBaseline)
        {
            _minions.Tick(dt);
            _neutral.Tick(dt);
        }
        // Dominion's quests run once per step, after the minions and camps they may read.
        if (!_combat.GameOver && !_visibilityBaseline) Quests?.Tick(_combat.CurrentTime, dt);
        // Past the game over too, so the step that fells the nexus can still complete its quest.
        if (!_visibilityBaseline) MapScript?.Invoke(_combat.CurrentTime);
        // The source pre-combat turret-vision phase reveals a shrine from
        // provider LOS or an existing valid target, and clears its team bit
        // before a hidden TargetLaser can be emitted.
        _delivery.PrepareShrineVisibility(_combat.CanAttack);
        _combat.CombatTick(dt);
        // Batch 497: map 8's nexus fall (the crystal prop's shatter) runs after the combat that may fell the nexus, and past the game over.
        if (!_visibilityBaseline) Quests?.TickNexusFall(_combat.CurrentTime);
        // The pet phase is the shipped scripts' step: each pet's 0.15 s timers, then the native
        // state's own walk and swing. The lane-less spawned units (Minion.lua, Aggro.lua) are not
        // pets and keep their own controller.
        _tibbers.Tick(_combat.CurrentTime, dt);
        _pets.Tick(_combat.CurrentTime, dt);
        _boxes.Tick(_combat.CurrentTime, dt);
        // Shop range follows pet/combat movement in the source phase, before
        // new spell work is resolved.
        foreach (var hero in _world.Entities.Values.Where(entity => entity.UnitType == UnitType.Hero).ToArray())
            _shop.UpdateAccess(hero);
        _spells.Tick(dt);
        // Batch 149 step 5: the hand-written item aura membership is gone with the twins it held (the Guardian
        // Angel self-row among them); the aura loops an item still needs are its own script's
        // UpdateSelfBuffStats, which the copy poll samples (BbItemModule.Poll).
        foreach (var entity in _world.Entities.Values.ToArray())
            _buffs.TickBuffs(entity, dt);
        // Ward, bubble and item-side timed effects follow buff ticking. Spell
        // cooldowns remain owned by SpellSystem, so ItemService does not
        // decrement those slots a second time.
        _items.Tick(dt, advanceCooldowns: false);
        _spells.TickLateChampions(dt);
        // The wire grace of the buff lifecycle: the removes held for this tick's re-adds go out now, the ones no re-add took (BuffLifecycle.EndTick).
        _buffs.EndTick();
        foreach (var hero in _world.Entities.Values.Where(entity => entity.UnitType == UnitType.Hero).ToArray())
            _runtime.HeroStats.TickSpellTooltips(hero, dt);
        _combat.ProcessRespawns();
        // SetBarracksSpawnEnabled(false) is EndOfGame.luaobj's own (proto 0.0 pc 0076-0077); the
        // match is over by then, but a script can switch the waves off before that too.
        if (!_combat.GameOver && _combat.BarracksSpawnEnabled && !_visibilityBaseline)
        {
            // The jungle's initial spawns come before the shared level host steps (the wave tick advances it), as the camp's own
            // host did before the merge; then the waves ask the script for their wave.
            _jungle.Tick(_combat.CurrentTime);
            _waves.Tick(_combat.CurrentTime);
        }
        ApplyDeferredOrders?.Invoke();
        PlaytestDiagnostics?.Tick();
    }
}
