using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Combat;
using System.Reflection;

namespace Amberline.Tests.Spells;

public static class ChampionContractChecks
{


    // 19 October 2026: "spells-champion-orianna-cooldown-ownership" is gone with the hand-written module; a script-driven champion's cooldowns are the
    // engine's (BbChampionModule.OwnsCooldown is false).

    [Check("spells-champion-b5-single-module-registry")]
    public static void ProductionRegistersAllChampionsAndRejectsDuplicateNames()
    {
        using var runtime = Amberline.Network.Sessions.MatchRuntime.CreateProduction(
            NavigationGrid.LoadDefault(), GameData.Default, "Annie", "Shaco");
        TestAssert.True(runtime.Dependencies.ChampionDispatcher is ChampionSpellDispatcher);
        var dispatcher = runtime.Dependencies.ChampionDispatcher;
        foreach (var name in ChampionCatalog.PlayableNames)
        {
            var module = dispatcher.FindModule(name);
            TestAssert.True(module is not null, name + " has an ordinary registered module");
            TestAssert.Throws<ArgumentException>(() => new ChampionSpellDispatcher([module!, module!]));
        }
        TestAssert.True(dispatcher.FindModule("Shaco") is BbChampionModule, "Shaco runs from his shipped scripts");
        TestAssert.True(dispatcher.FindModule("Yorick") is BbChampionModule, "Yorick runs from his shipped scripts (phase 3, 19 October 2026)");
        TestAssert.Throws<ArgumentException>(() => new ChampionSpellDispatcher([
            dispatcher.FindModule("Shaco")!, new OrderProbeModule([], ChampionTickPhase.Late, "sHaCo")]));
    }

    [Check("spells-annie-b5-module-shared-phase-and-state")]
    public static void AnnieUsesModuleAdmission()
    {
        // 19 October 2026: Annie runs from her shipped scripts (BbChampionModule), so her hand-written phase, state and Q-marker contract
        // is gone. See docs/maintenance/2026-10-19-w21-annie-script.md. What stays is the registration rule: a hero without a
        // registered module cannot cast, whatever its champion name.
        var world = new WorldState();
        var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(7000, 7000))
        {
            ChampionName = "Annie", Mana = 500, MaxMana = 500, Health = 500,
            SpellRanks = [1, 1, 1, 1],
        });
        var target = world.Register(new Entity(2, EntityKind.Monster, UnitType.Monster, Team.Chaos, new(7100, 7000))
            { Health = 500 });
        var movement = new MovementSystem(world, NavigationGrid.LoadDefault());
        var noAnnie = new SpellSystem(world, movement, new NullSpellCombat(), new NullSpellEffects(),
            new NullSpellBuffs(), new NullChampionHooks(), new ChampionSpellDispatcher([new BbChampionModule("Orianna")]));
        TestAssert.False(noAnnie.CanCast(hero, 0, target.NetId, new(7100, 0, 7000), 0).Allowed,
            "FW-02b: Annie cannot bypass registration");
    }

    [Check("spells-champion-b5-required-lifecycle-routing")]
    public static void ModuleLifecycleRetainsDeathPhasesAndTargetLifeReason()
    {
        foreach (var name in new[] { nameof(IChampionSpellModule.OnHeroDeath), nameof(IChampionSpellModule.RetireTargetedFlights) })
            TestAssert.True(typeof(IChampionSpellModule).GetMethod(name)!.IsAbstract,
                "FW-01: lifecycle implementation is required for every new module");
        var calls = new List<string>();
        var early = new OrderProbeModule(calls, ChampionTickPhase.Late, "Early");
        var late = new OrderProbeModule(calls, ChampionTickPhase.Late, "Late")
            { DeathPhase = ChampionDeathPhase.AfterControllerBuffs };
        var dispatcher = new ChampionSpellDispatcher([early, late]);
        var world = new WorldState();
        var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new(7000, 7000))
            { ChampionName = "Late" });
        var spells = new SpellSystem(world, new MovementSystem(world, NavigationGrid.LoadDefault()),
            new NullSpellCombat(), new NullSpellEffects(), new NullSpellBuffs(), new NullChampionHooks(), dispatcher);
        spells.OnHeroDeath(hero, ChampionDeathPhase.BeforeControllerBuffs);
        TestAssert.Equal(0, calls.Count, "post-controller death work must wait for its original phase");
        spells.OnHeroDeath(hero, ChampionDeathPhase.AfterControllerBuffs);
        TestAssert.Equal("death:Late:1", calls.Single());
        calls.Clear();
        TestAssert.Equal(2, spells.DestroyTargetedProjectiles(hero.NetId));
        TestAssert.Equal("flight:Early:1:Untargetable,flight:Late:1:Untargetable", string.Join(",", calls));
        calls.Clear();
        hero.Alive = false;
        TestAssert.Equal(2, spells.DestroyTargetedProjectiles(hero.NetId));
        TestAssert.Equal("flight:Early:1:TargetDeath,flight:Late:1:TargetDeath", string.Join(",", calls));
    }

    [Check("spells-champion-b5-shared-control-capacity")]
    public static void SharedControlCapacityReservesOnlyTheHostileHeroBootsSlot()
    {
        var caster = new Entity { Team = Team.Order };
        var target = new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Chaos, new(0, 0))
            { Inventory = [new Amberline.Simulation.InventorySlot(3117), null, null, null, null, null] };
        for (var slot = 0; slot < SpellPackets.MaxBuffSlots - 1; slot++)
            target.Buffs.Add(new BuffState($"Capacity{slot}", slot: slot, expiresAt: 60));
        TestAssert.False(ChampionSpellSupport.CanAddControl(caster, target, "Stun"),
            "FW-06: a hostile hero needs capacity for both control and Mobility Boots");
        target.Team = Team.Order;
        TestAssert.True(ChampionSpellSupport.CanAddControl(caster, target, "Stun"));
        target.Team = Team.Chaos;
        target.Inventory = [null, null, null, null, null, null];
        TestAssert.True(ChampionSpellSupport.CanAddControl(caster, target, "Stun"));
        target.Inventory = [new Amberline.Simulation.InventorySlot(3117), null, null, null, null, null];
        target.Buffs.RemoveAt(0);
        TestAssert.True(ChampionSpellSupport.CanAddControl(caster, target, "Stun"));
        target.Buffs.Add(new BuffState("BootsOfMobilityDebuff", slot: 0, expiresAt: 60));
        TestAssert.True(ChampionSpellSupport.CanAddControl(caster, target, "Stun"),
            "replacing an existing Boots marker only needs the control slot");
    }

    [Check("spells-leesin-b5-data-cost-and-damage")]
    public static void LeeRankTwoUsesIsolatedSpellData() =>
        // 19 October 2026: the check runs on the roster-based Match of ChampionLiveRegressionChecks: Lee Sin's script module binds to a match when
        // its first Lee Sin hero is created, which a hand-registered entity never triggers.
        ChampionLiveRegressionChecks.VerifyLeeIsolatedSpellData();

    [Check("spells-champion-dispatch-contract")]
    public static void DispatcherExposesPerChampionRankAndSpellContracts()
    {
        // 19 October 2026: Lee Sin is a script champion (production registers BbChampionModule("LeeSin")); the default list holds the hand-written ones.
        var dispatcher = ChampionSpellDispatcher.CreateDefault((_, _) => true, [new BbChampionModule("LeeSin")]);
        var lee = new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(0, 0))
        {
            ChampionName = "LeeSin",
        };

        TestAssert.True(dispatcher.Handles(lee));
        TestAssert.Equal("BlindMonkRKick", dispatcher.SpellName(lee, 3));
        TestAssert.Equal(3, dispatcher.MaxRank(lee, 3));
        TestAssert.False(dispatcher.OwnsCooldown(lee, 0), "the script module leaves Lee Sin's cooldowns to the spell system (BlindMonkQManager sets them)");
        TestAssert.Equal(ChampionTickPhase.AfterCooldowns, dispatcher.TickPhase(lee));
    }

    [Check("spells-champion-state-isolation")]
    public static void ChampionStatesAreTypedAndPerEntity()
    {
        var first = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(10, 20));
        var second = new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(30, 40));
        // 19 October 2026: Orianna, Lee Sin, Shaco, Nidalee and Yorick have no typed state any more (their scripts keep theirs in buffs), so the
        // isolation rules are checked on a stand-in state; the "one entity cannot bind two action states" leg needs a second typed state and is gone.
        var firstState = new StandInChampionState();
        var secondState = new StandInChampionState();
        first.ChampionState = firstState;
        second.ChampionState = secondState;
        TestAssert.True(ReferenceEquals(first.ChampionState, firstState), "the state must be the movement/cast state");
        TestAssert.True(ReferenceEquals(second.ChampionState, secondState), "state binding must be per entity");
        TestAssert.False(ReferenceEquals(first.ChampionState, second.ChampionState), "one entity's state is not the other's");

        first.YorickRevival = new YorickRevivalState(1.0);
        TestAssert.True(ReferenceEquals(first.ChampionState, firstState),
            "an external Yorick revival must not replace the marked hero state");
        first.ChampionState = null;
        TestAssert.True(first.ChampionState is null,
            "clearing a state must clear the authoritative state");
    }

    [Check("spells-composite-late-order")]
    public static void CompositeLatePhasePreservesWorldHeroOrder()
    {
        var world = new WorldState();
        world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order,
            new Position2(0, 0)) { ChampionName = "Yorick" });
        world.Register(new Entity(2, EntityKind.Hero, UnitType.Hero, Team.Chaos,
            new Position2(10, 0)) { ChampionName = "Shaco" });
        world.Register(new Entity(3, EntityKind.Hero, UnitType.Hero, Team.Chaos,
            new Position2(20, 0)) { ChampionName = "Nidalee" });
        world.Register(new Entity(4, EntityKind.Hero, UnitType.Hero, Team.Order,
            new Position2(30, 0)) { ChampionName = "Orianna" });

        // FW-02a: retain the original late-order assertions through the single registry.
        var calls = new List<string>();
        var yorick = new OrderProbeModule(calls, ChampionTickPhase.Late, "Yorick");
        var shaco = new OrderProbeModule(calls, ChampionTickPhase.Late, "Shaco");
        var nidalee = new OrderProbeModule(calls, ChampionTickPhase.Late, "Nidalee");
        var orianna = new OrderProbeModule(calls, ChampionTickPhase.BeforeCooldowns, "Orianna");
        var dispatcher = new ChampionSpellDispatcher([nidalee, shaco, yorick, orianna]);
        var movement = new MovementSystem(world, NavigationGrid.LoadDefault());
        var spells = new SpellSystem(world, movement, new NullSpellCombat(),
            new NullSpellEffects(), new NullSpellBuffs(), new NullChampionHooks(),
            dispatcher, GameData.Default);

        spells.TickLateChampions(0.1);

        TestAssert.Equal(3, calls.Count, "late phase callback count");
        TestAssert.Equal("Yorick:1", calls[0], "first late hero follows world order");
        TestAssert.Equal("Shaco:2", calls[1], "second late hero follows world order");
        TestAssert.Equal("Nidalee:3", calls[2], "third late hero follows world order");
    }

    private sealed class StandInChampionState : IChampionState
    {
        public bool BlocksActions => false;
        public bool IsReviving => false;
    }

    private sealed class OrderProbeModule(List<string> calls,
        ChampionTickPhase phase, string champion) : IChampionSpellModule
    {
        public ChampionDeathPhase DeathPhase { get; init; } = ChampionDeathPhase.BeforeControllerBuffs;
        public void OnHeroDeath(SpellSystem system, Entity hero) => calls.Add($"death:{champion}:{hero.NetId}");
        public int RetireTargetedFlights(SpellSystem system, Entity hero, uint targetNetId,
            TargetedFlightRetirement reason)
        {
            calls.Add($"flight:{champion}:{targetNetId}:{reason}");
            return 1;
        }
        public string ChampionName => champion;
        public bool Handles(Entity hero) => hero.ChampionName == champion;
        public string SpellName(Entity hero, int slot) => "probe";
        public int MaxRank(Entity hero, int slot) => 5;
        public int RankGate(Entity hero, int slot, int rank) => 1;
        public SpellValidation Validate(SpellSystem system, Entity hero, int slot,
            uint targetNetId, Position3 position, double now, Position3? end = null) => SpellValidation.Success;
        public bool Execute(SpellSystem system, Entity hero, SpellCastRequest request, double now) => true;
        public void Tick(SpellSystem system, Entity hero, double dt) =>
            calls.Add($"{hero.ChampionName}:{hero.NetId}");
        public ChampionTickPhase TickPhase => phase;
        public bool OwnsCooldown(Entity hero, int slot) => false;
        public void OnRankUp(SpellSystem system, Entity hero, int slot, int rank) { }
    }

    private sealed class NullSpellCombat : ISpellCombatService
    {
        public List<double> Damage { get; } = [];
        public bool IsTargetable(Entity target, Entity? observer) => true;
        public double HealingMultiplier(Entity target) => 1.0;
        public bool ApplyDamage(Entity source, Entity target, double amount, DamageType type,
            DamageSourceKind? sourceKind, double vampScale, bool isReflect,
            bool sourceIsPeriodic, uint? spellOwnerNetId)
        {
            Damage.Add(amount);
            return true;
        }
        public bool ApplyDamage(Entity source, Entity target, double amount, DamageType type,
            DamageSourceKind? sourceKind, DeathRecapOrigin recapOrigin, bool bypassSpellShield)
        {
            Damage.Add(amount);
            return true;
        }
        public bool ConsumeSpellShield(Entity source, Entity target, bool force) => false;
        public void InterruptBasicAttack(Entity attacker) { }
        public void Kill(Entity unit, uint killerNetId) { }
    }

    private sealed class NullSpellBuffs : ISpellBuffService
    {
        public void ScriptStatusGained(Entity unit, bool wasHardControlled, bool wasRooted) { }
        public void ScriptStatusLost(Entity unit, bool couldMove, bool couldAttack) { }
        public List<(string Name, BuffPayload? Payload)> Added { get; } = [];
        public BuffState? Add(Entity unit, string name, BuffType type, double duration,
            uint casterNetId, BuffPayload? payload = null, bool canMitigateDuration = true)
        {
            Added.Add((name, payload));
            return null;
        }
        public BuffState? AddStacking(Entity unit, string name, BuffType type, double duration,
            uint casterNetId, int maxStacks, BuffPayload perStackPayload,
            bool replaceExistingPayload = false, bool canMitigateDuration = true) => replaceExistingPayload
                ? throw new NotSupportedException("null spell-buff test double cannot renew payloads")
                : null;
        public bool Remove(Entity unit, int slot) => false;
        public bool Has(Entity unit, string name) => false;
        public void Tick(Entity unit, double dt) { }
        public void RefreshMovementSpeed(Entity unit, bool push = true) =>
            throw new NotSupportedException(nameof(RefreshMovementSpeed));
    }

    private sealed class NullChampionHooks : IChampionSpellHooks
    {
        public void OnSpellCast(Entity hero, int slot, double now) { }
        public void OnNamedSpellCast(Entity hero, int slot, string spellName, Entity? target) { }
        public bool CastPyromania(Entity hero, bool damaging) => false;
        public void CancelChannel(Entity hero, string reason) { }
    }

    private sealed class NullSpellEffects : ISpellEffectSink
    {
        public List<string> CastEvents { get; } = [];
        public bool IsReferenceReady(Entity unit) => true;
        public void SendCast(Entity caster, SpellCastInfo info, long casterPositionSyncId = 0, bool missileOnlyForCaster = false) => CastEvents.Add("cast");
        public void SendMissile(Entity caster, SpellCastInfo info, Position3 position,
            Position3 direction, Position3 velocity, Position3 startPoint,
            Position3 endPoint, Position3? unitPosition = null,
            Position3? casterPosition = null, bool needsCastAnnouncement = false) { }
        public void SendWaypoints(Entity unit, IReadOnlyList<Position2> points, long syncId) => CastEvents.Add("waypoints");
        public void SendStopAttack(Entity unit) { }
        public void SendCooldown(Entity unit, int slot, double seconds, bool summoner = false) => CastEvents.Add("cooldown");
        public void SendUpgrade(Entity unit, int slot, int rank, int pointsLeft) { }
        public void SendFaceDirection(Entity unit, Position3 direction) { }
        public void SendCharacterFade(Entity unit, short fadeId, float? amount = null,
            float duration = 0.0f) { }
        public void SendTeleport(Entity unit, Position2 destination, byte teleportId,
            long syncId, Position2? staging) { }
        public void SendCamera(Entity unit, Position2 position) { }
        public void SendSpawn(Entity unit) { }
        public void SendPetSpawn(Entity unit, SpawnPetSnapshot snapshot) { }
        public void SendStats(Entity unit) => CastEvents.Add("stats");
        public void SendAttackSpeed(Entity unit) { }
        public void SendMovementSpeed(Entity unit) { }
        public void SendHealth(Entity unit, double health) { }
        public uint CreateEffect(string effectName, Position2 position, Position2? target = null,
            uint targetNetId = 0, uint bindNetId = 0, Position3 direction = default,
            Position2? ownerPosition = null, Team? alliedTeam = null, string? enemyEffectName = null,
            string? boneName = null, bool persistent = false, uint ownerHeroNetId = 0,
            Func<(Position2 Source, Position2 Target)>? livePositions = null,
            ushort flags = 0, string? targetBoneName = null, UnboundEffectFallback? unbound = null, Team? fowTeam = null) => 0;
        public void KillEffect(uint effectNetId) { }
        public void SendBuffDelta(Entity unit, BuffState buff, bool added, int count = 1) { }
        public void SendShieldDelta(Entity unit, double amount) { }
        public void PauseAnimation(Entity unit, bool paused) { }
        public void PlayAnimation(Entity unit, string name, bool loop = false, bool lockAnimation = false,
            bool blend = true, float duration = 0) { }
        public void StopAnimation(Entity unit, bool blend = true) { }
        public void SendDestroyMissile(uint missileNetId) { }
        public void SendFinishCast(Entity unit) { }
    }
}
