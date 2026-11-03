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

/// <summary>All state created by the maintained production composition root.</summary>
internal sealed class ProductionMatchGraph
{
    public required NavigationGrid Navigation { get; init; }
    public required MatchRuntimeDependencies Dependencies { get; init; }
    public required GameData GameData { get; init; }
    public required WorldState World { get; init; }
    public required LifecycleMonitor Lifecycle { get; init; }
    public required ReplicationSyncCounter SyncIds { get; init; }
    public required LegacyBlowfishCipher Cipher { get; init; }
    public required MovementSystem Movement { get; init; }
    public required ReplicationDelivery Delivery { get; init; }
    public required HeroStatsService HeroStats { get; init; }
    public required ProtocolCombatTransport CombatTransport { get; init; }
    public required CombatService Combat { get; init; }
    public required ProtocolSpellEffectSink Effects { get; init; }
    public required BuffLifecycle Buffs { get; init; }
    public required ISpellCombatService SpellCombat { get; init; }
    public required ISpellBuffService SpellBuffs { get; init; }
    public required SpellSystem Spells { get; init; }
    public required BotController Bots { get; init; }
    public required DuelSession Session { get; init; }
    public required ISessionMovementService SessionMovement { get; init; }
    public required ISessionCombatService SessionCombat { get; init; }
    public required ISessionEffectService SessionEffects { get; init; }
    public required TibbersAiSystem Tibbers { get; init; }
    public required SharedCloneService Clones { get; init; }
    public required PetAiService Pets { get; init; }
    public required MinionAiSystem Minions { get; init; }
    public required WaveScheduler Waves { get; init; }
    public required ProductionSimulationSpeed SimulationSpeed { get; init; }
    public CollisionDiagnosticSink? MovementTrace { get; init; }
    public FidelityTrace? FidelityTrace { get; init; }
    public required PlaytestDiagnostics PlaytestDiagnostics { get; init; }
}

/// <summary>
/// Creates one complete match graph in dependency order.  Service callbacks
/// that close the unavoidable delivery cycle are explicit holders and throw
/// until the graph has reached its attach point.
/// </summary>
internal static class ProductionMatchComposition
{
    private const double WormLairBubbleRadius = 1600.0;

    public static MatchRuntime Build(
        NavigationGrid navigation,
        GameData? gameData,
        string orderChampion,
        string chaosChampion,
        bool solo,
        SessionRosterCounts? originalRosterCounts,
        double readySettleSeconds,
        IRandomSource? random,
        LegacyBlowfishCipher? cipher,
        bool visibilityBaseline,
        bool minionFidelity,
        ISessionLogSink? sessionLog,
        IReadOnlyList<SessionParticipantDefinition>? roster,
        ulong soloPlayerId,
        IMovementDiagnosticSink? movementDiagnostics = null,
        bool botsControlAllSeats = false,
        Func<double>? scriptClock = null,
        int mapId = GameMaps.Normal)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        var data = gameData ?? GameData.Default;
        var matchCipher = cipher ?? LegacyBlowfish.CreateDefault();
        var world = new WorldState();
        var fidelityTrace = FidelityTrace.FromEnvironment(world,
            sessionLog is null ? null : batch => sessionLog.Write(
                "AI", "DEBUG", "fidelity_batch", FidelityTrace.ToProperties(batch)),
            cutoff => sessionLog?.Write("AI", "INFO", "fidelity_cutoff", new Dictionary<string, object?>
            {
                ["mono_ns"] = cutoff.MonotonicNanoseconds,
                ["budget_bytes"] = cutoff.BudgetBytes,
                ["written_bytes"] = cutoff.WrittenBytes,
                ["rejected_batch_bytes"] = cutoff.RejectedBatchBytes,
                ["published_batches"] = cutoff.PublishedBatches,
                ["compact_movement_diagnostics_enabled"] =
                    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LOG_COLLISION")),
            }));
        var collisionTrace = CollisionDiagnosticSink.FromEnvironment(world, sessionLog,
            new NavigationBounds(navigation.MinX, navigation.MaxX, navigation.MinZ, navigation.MaxZ));
        var minionTrace = MinionTraceSink.FromEnvironment(world, sessionLog);
        var lifecycle = new LifecycleMonitor(world);
        var sync = new ReplicationSyncCounter();
        // DIAGNOSTICS (batch 297): inside a check a match without a random source takes the check's seed.
        var rng = random ?? (SeededRandomSource.CheckSeed is int checkSeed
            ? new SeededRandomSource(checkSeed) : new SystemRandomSource());
        var holder = new ProductionServices(world, data, navigation, sync);
        collisionTrace.HudTime = () => holder.Session?.HudTime;
        var hooks = new MatchGameplayHooks(world, data,
            (team, point) => holder.ItemVision.RevealsStealthAt(team, point),
            winner =>
            {
                var session = holder.Session ?? throw new InvalidOperationException("session was not composed");
                if (session.RecordResult(winner, holder.Combat!.Surrendered) is { } result) sessionLog?.MatchCompleted(result);
            });

        var diagnostics = new MovementDiagnosticFanout(fidelityTrace, collisionTrace,
            movementDiagnostics);
        var movement = new MovementSystem(world, navigation, diagnosticSink: diagnostics);
        // Batch 608: the client's own movement code from the player's League of Legends.exe, or the server's fallback (logged).
        movement.StartEngine(data.Paths, mapId, (level, message, properties) => sessionLog?.Write("MOVEMENT", level, message, properties));
        var playtest = new PlaytestDiagnostics(world, navigation, movement, sessionLog,
            enabled: Environment.GetEnvironmentVariable("LOG_INVARIANTS") != "0");
        playtest.HudTime = () => holder.Session?.PlaytestHudTime;
        if (playtest.Enabled) movement.StepObserver = playtest.ObserveMovement;
        movement.DecisionObserver = collisionTrace.NoteDecision;
        var replication = new ProtocolReplicationServices(
            (entity, observer) => holder.BuildCreation(entity),
            (entity, observer) => holder.BuildStats(entity, observer),
            (entity, observer) => holder.BuildBuffs(entity, observer),
            (ball, observer) => holder.BuildBall(ball),
            sync.Next,
            new NavigationBounds(navigation.MinX, navigation.MaxX,
                navigation.MinZ, navigation.MaxZ),
            // Routes (the reveal's movement data, C1 reanchors) carry the match clock's global sync id.
            // CC stats/protection packets use the per-entity replication
            // sequence above; sharing it consumes a stats ID on every reveal.
            _ => checked((uint)world.Clock.SyncId()),
            (entity, move) => movement.FirstCollisionDashLanding(entity, move));
        var environment = new NavigationGridVisionEnvironment(navigation);
        var delivery = new ReplicationDelivery(world, replication, environment,
            team => holder.ItemVision.Providers(team),
            (team, point) => holder.ItemVision.RevealsStealthAt(team, point),
            nextSyncId: sync.Next);
        holder.Delivery = delivery;
        delivery.AfterVisibilityFlush = () =>
        {
            fidelityTrace?.FlushPostVisibility();
            collisionTrace.FlushPostVisibility();
        };

        var catalog = new ItemCatalog(data.Paths);
        var stats = new HeroStatsService(data, catalog, hooks,
            new ProtocolHeroStatsTransport(delivery, sync));
        holder.HeroStats = stats;
        var combatTransport = new ProtocolCombatTransport(delivery, stats,
            entity => entity.Created, sync) { GroundHeight = point => navigation.HeightAt(point) };
        var combat = new CombatService(world, hooks, combatTransport, rng, stats, gameData: data);
        // Every AI unit runs its shipped script (Turret.luaobj today) through the match's Lua AI
        // host; a script that raises is reported the way the BuildingBlocks spells already are.
        combat.AiScriptError = failure => sessionLog?.Write("GAMEPLAY", "ERROR", "script.exception",
            new Dictionary<string, object?>
            {
                ["script"] = failure.Script, ["hook"] = failure.Hook, ["message"] = failure.Message,
            });
        delivery.SimulationTime = () => combat.CurrentTime;
        delivery.DeathRecapSnapshotProvider = combat.GetLastDeathRecap;
        combat.EndGameBallNoRender = delivery.SetTeamBallNoRender;
        holder.Combat = combat;
        var spellCombat = new CombatSpellAdapter(combat);
        var bounds = new NavigationBounds(navigation.MinX, navigation.MaxX,
            navigation.MinZ, navigation.MaxZ);
        var effects = new ProtocolSpellEffectSink(delivery, sync, bounds,
            (x, z) => navigation.HeightAt(new Position2(x, z)), world.Allocate,
            unit =>
            {
                foreach (var packet in holder.BuildStats(unit))
                    delivery.Enqueue(packet);
            },
            (snapshot, _) =>
            {
                var team = snapshot.Team == TeamMapping.ToNative(Team.Order)
                    ? Team.Order : Team.Chaos;
                var projection = new BallProjection(snapshot.NetId, snapshot.OwnerNetId,
                    team, snapshot.Position, Rendered: true, SkinId: snapshot.SkinId);
                delivery.SetManagedBallProjection(projection);
            },
            delivery.RemoveManagedBallProjection, data, delivery.IsBallNoRender,
            unit =>
            {
                foreach (var packet in holder.BuildCombatState(unit))
                    delivery.Enqueue(packet);
            });
        var buffVision = new ProtocolBuffVisionSink(world.Get, world.Allocate, delivery);
        var buffEffects = new BuffEffectService(effects, buffVision,
            entity => world.Clock.SyncId());
        holder.BuffEffects = buffEffects;
        hooks.AttachBuffEffectHooks(buffEffects);
        var buffs = new BuffLifecycle(world, stats, hooks.BuffServices,
            movement.ClientAnchor);
        var spellBuffs = new BuffLifecycleAdapter(buffs);

        var minionWire = new ProductionMinionPacketSink(navigation, delivery, sync,
            fidelityTrace, collisionTrace, () => world.Clock.Monotonic(),
            (entity, move) => movement.FirstCollisionDashLanding(entity, move),
            () => world.Entities.Values);
        var minionCombat = new ProductionMinionCombatService(combat);
        var minionVision = new WorldMinionVisionService(world, navigation,
            team => holder.ItemVision.Providers(team));
        var minions = new MinionAiSystem(world, navigation, movement, minionCombat,
            minionWire, minionVision, minionTrace, canSee: TeamSight, random: rng,
            targetCost: data.LevelConstants.TargetCost);
        buffs.Minions = minions;
        buffs.AiScripts = combat.AiHost;
        minions.DecisionObserver = collisionTrace.NoteDecision;
        combat.TargetLost = minions.NotifyTargetLost;
        // Map1's shipped LevelScript.luaobj owns the wave schedule: what a wave is made of, when
        // the next one is due and how its members are staggered. Its failures go to the session
        // log the way the BuildingBlocks spells and Turret.luaobj already do.
        var level = new LevelScriptHost(world, catalog: new AiScriptCatalog(data.Paths), navigation: navigation)
        {
            // Batch 222: the caller's clock (the shipped server passes the wall clock). Without one, a seeded match keeps its seed:
            // the tool and check seam, labelled DIAGNOSTICS, because retail's os.time is the wall clock. Batch 297: a check
            // that passes its own unseeded rolls still gets the check's seed, so LevelScript's math.randomseed repeats.
            ScriptClock = scriptClock ?? (rng is SeededRandomSource seeded
                ? () => seeded.Seed
                : SeededRandomSource.CheckSeed is int clockSeed
                    ? () => clockSeed
                    : (Func<double>)(() => DateTimeOffset.UtcNow.ToUnixTimeSeconds())),
            OnError = failure => sessionLog?.Write("GAMEPLAY", "ERROR", "script.exception",
                new Dictionary<string, object?>
                {
                    ["script"] = failure.Script, ["hook"] = failure.Hook, ["message"] = failure.Message,
                }),
        };
        combat.BarracksChanged = (barracks, destroyed) =>
        {
            if (destroyed) level.OnBarracksFell(barracks);
            else level.OnBarracksRestored(barracks);
        };
        level.DisableMinionSpawn = combat.ArmInhibitorReturn;
        // EndOfGame.luaobj lives in the level host's own environment (Map1's OnLevelInit chains
        // it in), so the ceremony's natives are that host's and its engine half is combat: the
        // two entry points the match runs are the ceremony call and its phase clock.
        level.EndOfGameEngine = combat;
        combat.EndOfGameScript = level;
        // The map's protection ladder is ProtectionRules — our port of the script's own
        // HandleDestroyedObject/DeactivateCorrectStructure — recomputed from world state. Let it
        // reconcile anything the level script's structure natives moved.
        level.StructureStateChanged = combat.RecomputeProtection;
        // The camps' GiveExpToNearHeroesFromNeutral pays through the combat service's award, so the experience bonus and the
        // level-ups apply to a camp's share as they do to any other award (batch 219).
        level.AwardNeutralExperience = (hero, experience) => combat.AwardHeroProgress(hero, experience: experience);
        // OnPostLevelLoad chains Map1's CreateLevelProps.lua and runs its CreateLevelProps(): the
        // twenty-four CreateChildTurret rows that name this map's turrets. The engine has already
        // built them from those same rows (MapDefinitions.Turrets), so this is the match running
        // its own map script, not a second set of buildings.
        level.OnPostLevelLoad();
        // NOT RETAIL: The Crystal Scar (map 8) plays no lane waves in phase 1: its waves are Odin minions on Odin lanes, which need the Odin
        // minion AI (batch 441 research, phase 4). The skip is logged, and its lanes are not Summoner's Rift's.
        // Reconstruction (batch 563): the Basic Tutorial (map 3) plays no lane waves either. Its LevelScript sets INITIAL_TIME_TO_SPAWN
        // -90000000 (LEVELS/Map3/LevelScript.txt pc 0020) and its TutorialScript spawns every minion its lessons use with SpawnMinion
        // (SpawnStartingMinions, SpawnOrderMinions, SpawnChaosMinions, MinionSwarm); what the engine's barracks did with the negative
        // first wave time is not in the shipped files.
        var laneWaves = mapId is not (GameMaps.CrystalScar or GameMaps.Tutorial);
        if (!laneWaves)
            sessionLog?.Write("GAMEPLAY", "INFO", "map.lane-waves-skipped", new Dictionary<string, object?>
            {
                ["map"] = mapId,
                ["reason"] = mapId == GameMaps.Tutorial
                    ? "map 3's minions are the TutorialScript's (SpawnMinion), INITIAL_TIME_TO_SPAWN is -90000000"
                    : "map 8's waves are its Odin waves (SpawnMinionWave on the scene lanes)",
            });
        var waves = new WaveScheduler(world, navigation, minionWire, level, gameData: data, buffs: buffs,
            laneMinionSkinId: mapId == GameMaps.Harrowing ? GameMaps.HarrowingMinionSkinId : 0, laneWaves: laneWaves);
        // Route stitching is map work, not a first-wave side effect. Warm all
        // six team/lane routes while the match graph is still being built so
        // the first live traffic tick cannot pay a pathfinding cost.
        if (laneWaves) waves.WarmAllRoutes();
        var neutralSink = new ProtocolNeutralPacketSink(packet => delivery.Enqueue(packet), sync,
            world.Allocate, bounds,
            (x, z) => navigation.HeightAt(new Position2(x, z)), data.ClientBuffName, data.HasClientBuff);
        var scheduler = new JungleScheduler(world, data, neutralSink, navigation: navigation, levelScript: level);
        // Empty-camp observation is resolved by ReplicationDelivery, which
        // owns ready peers and team LOS.  Keep the scheduler state authoritative
        // without making the neutral sink broadcast a global 0xCB packet.
        delivery.NeutralCamps = scheduler.Camps;
        holder.Scheduler = scheduler;
        // Reconstruction: how the retail neutral AI decides it still sees its target is native. A unit senses
        // through its perception bubble (CharacterData PerceptionBubbleRadius, 1350 for every camp monster) with
        // line of sight, plus the 400-unit bubble a basic attack or flagged spell leaves at the attacker's
        // position for ca_RevealAttackerTimeOut (RevealSpellCaster; VisibilityRules.BuildTeamProviders). Both
        // had no range before, so a camp chased a hero 2,640 units away (J-AUDIT F2).
        bool NeutralTargetVisible(Entity monster, Entity target)
        {
            if (!combat.IsTargetable(target, monster)) return false;
            var team = (int)monster.Team;
            var reveal = target.AttackRevealPosition[team];
            if (target.AttackRevealRemaining[team] > 0.0 &&
                (reveal.X - target.Position.X) * (reveal.X - target.Position.X) +
                (reveal.Z - target.Position.Z) * (reveal.Z - target.Position.Z) <= 400.0 * 400.0)
                return true;
            // The Worm's CharOnActivate (Characters/Worm/Scripts/CharScriptWorm.luaobj 0455-0475) adds
            // BBAddPosPerceptionBubble(TEAM_NEUTRAL, radius 1600, at its own position, 25000 s, no true sight): a fixed bubble at the
            // lair for the neutral team, on top of its own 1350. It is the only camp monster whose script adds one (A2 finding J-15).
            if (monster.Name == "Worm" && monster.NeutralHome is { } lair &&
                (lair.X - target.Position.X) * (lair.X - target.Position.X) +
                (lair.Z - target.Position.Z) * (lair.Z - target.Position.Z) <= WormLairBubbleRadius * WormLairBubbleRadius &&
                navigation.HasUnitVision(new Position2(lair.X, lair.Z), monster.SelectionRadius,
                    target.Position, target.SelectionRadius))
                return true;
            var perception = data.Champion(monster.Name).PerceptionBubbleRadius;
            var dx = monster.Position.X - target.Position.X;
            var dz = monster.Position.Z - target.Position.Z;
            return dx * dx + dz * dz <= perception * perception &&
                navigation.HasUnitVision(monster.Position, monster.SelectionRadius,
                    target.Position, target.SelectionRadius);
        }
        var neutral = new NeutralAiService(world, scheduler, movement, combat,
            neutralSink, effects, spellBuffs, data, rng, NeutralTargetVisible);
        // Leashed.luaobj runs on its own host, one instance per camp member; its failures are
        // reported the same way the BuildingBlocks spells and Turret.luaobj already are.
        neutral.AiScriptError = failure => sessionLog?.Write("GAMEPLAY", "ERROR", "script.exception",
            new Dictionary<string, object?>
            {
                ["script"] = failure.Script, ["hook"] = failure.Hook, ["message"] = failure.Message,
            });
        buffs.Neutrals = neutral;
        var itemTransport = new ProtocolItemRuntimeTransport(delivery,
            entity => entity.Created, navigation, sync);
        var items = new ItemService(world, catalog, buffs, buffEffects, spellCombat,
            effects, itemTransport, navigation, data);
        var itemVision = new ItemVisionService(world);
        holder.ItemVision = itemVision;
        IEnumerable<VisionProvider> TeamVision(Team team) =>
            VisibilityRules.BuildTeamProviders(world, team, delivery.Balls, holder.ItemVision.Providers);
        var passives = new ItemPassiveService(world, data, buffs, buffEffects,
            spellCombat, effects, stats, rng);
        var damage = new ItemDamageService(world, buffs, buffEffects, passives,
            spellCombat, effects);

        var clones = new SharedCloneService(world, movement, combat, effects, data);
        var boxes = new ShacoBoxService(world, movement, combat, effects, rng, navigation, data);
        // Recipient callbacks retain their legacy order; hero ticks follow world order.
        var dispatcher = new ChampionSpellDispatcher([
            new BbChampionModule("Shaco", data),
            // BuildingBlocks engine (M2): Soraka runs from her shipped scripts. Her hooks join the match only when she is in it.
            // Phase 3 (19 October 2026): Annie is the pilot conversion, her shipped scripts are the only Annie (the hand-written module is gone).
            new BbChampionModule("Annie", data), new BbChampionModule("Soraka", data),
            // Phase 3 (19 October 2026, W27): Blitzcrank runs from his shipped scripts; the hand-written module is gone.
            new BbChampionModule("Blitzcrank", data),
            // Phase 3 (19 October 2026, W36): Orianna runs from her shipped scripts, the ball included (a spawned placemarker); the hand-written module is gone.
            new BbChampionModule("Orianna", data),
            // Phase 3 (19 October 2026, W32): Lee Sin's shipped scripts are the only Lee Sin (the hand-written module is gone).
            new BbChampionModule("LeeSin", data),
            // BuildingBlocks engine (M3): the next script-driven champions, each registered once its whole kit runs.
            new BbChampionModule("Taric", data), new BbChampionModule("Kayle", data), new BbChampionModule("Ryze", data),
            new BbChampionModule("Sivir", data),
            // Phase 3 (19 October 2026): Janna and Vayne run from their shipped scripts too (the hand-written modules are gone).
            new BbChampionModule("Janna", data),
            new BbChampionModule("Vayne", data),
            // Phase 3 (19 October 2026): Ezreal runs from his shipped scripts (the hand-written module is gone).
            new BbChampionModule("Ezreal", data),
            // Phase 3 (19 October 2026): Yorick runs from his shipped scripts, the ghouls and the revenant among them (the hand-written module is gone).
            new BbChampionModule("Yorick", data),
            // Phase 3 (19 October 2026): Nidalee runs from her shipped scripts too (the hand-written module is gone).
            new BbChampionModule("Nidalee", data),
            // Phase 3, new champions (19 October 2026, W37): Kassadin runs from his shipped scripts (wave 1, no engine work planned).
            new BbChampionModule("Kassadin", data),
            // Phase 3, new champions (19 October 2026, wave 1, W38): Udyr runs from his shipped scripts (no hand-written module ever existed).
            new BbChampionModule("Udyr", data),
            // Phase 3, new champions (19 October 2026, W33c): Morgana runs from her shipped scripts (wave 2; the E01 render and call-for-help statuses are the batch 110 no-ops).
            new BbChampionModule("Morgana", data),
            // Phase 3, new champions (19 October 2026, wave 2, W32c): Xin Zhao runs from his shipped scripts (no hand-written module ever existed).
            new BbChampionModule("XinZhao", data),
            // Phase 3, new champions (19 October 2026, wave 3, W23b): Amumu runs from his shipped scripts (no hand-written module ever existed).
            new BbChampionModule("Amumu", data),
            // Phase 3, new champions (19 October 2026, W26d): Alistar runs from his shipped scripts (wave 3; one of the 13 retail bots).
            new BbChampionModule("Alistar", data),
            // Phase 3, new champions (19 October 2026, wave 2, W19f): Brand runs from his shipped scripts (no hand-written module ever existed).
            new BbChampionModule("Brand", data),
            // Phase 3, new champions (19 October 2026, wave 5, W24b): Twisted Fate runs from his shipped scripts (no hand-written module ever existed).
            new BbChampionModule("TwistedFate", data),
            // Phase 3, new champions (19 October 2026, wave 8, W35c): Tryndamere runs from his shipped scripts (the rage pool is the engine's E20/E14 work of batch 125).
            new BbChampionModule("Tryndamere", data),
            // Phase 3, new champions (19 October 2026, W31c): Riven runs from her shipped scripts (wave 2; the engine bundle is batch 110, the reset duration batch 116).
            new BbChampionModule("Riven", data),
            // Phase 3, new champions (19 October 2026, wave 2, W30b): Zilean runs from his shipped scripts (no hand-written module ever existed).
            new BbChampionModule("Zilean", data),
            // Phase 3, new champions (25 October 2026, batch 214): Garen runs from his shipped scripts (no hand-written module ever existed).
            new BbChampionModule("Garen", data),
            // Phase 3, new champions (25 October 2026, batch 215, wave 4): Master Yi runs from his shipped scripts (Meditate is a channel with the
            // wave 4 hooks, Highlander the kill and assist hooks of wave 3; the retail bot runs the shipped Bot and Task scripts).
            new BbChampionModule("MasterYi", data),
            // Phase 3, new champions (25 October 2026, wave 4, batch 216): Miss Fortune runs from her shipped scripts (E08 channel, E13 cast adjusters; a retail bot).
            new BbChampionModule("MissFortune", data),
            // Phase 3, new champions (25 October 2026, wave 4, batch 224): Shen runs from his shipped scripts (E07 Vorpal Blade, E08 Stand United; a retail bot).
            new BbChampionModule("Shen", data),
            // Phase 3, new champions (25 October 2026, wave 6, batch 237): Ashe runs from her shipped scripts (E02, E04, E06 and E12 of the wave plan; a retail bot).
            new BbChampionModule("Ashe", data),
            // Phase 3, new champions (25 October 2026, batch 238): Cho'Gath runs from his shipped scripts (a retail bot; E06 kill hooks, E07 CharOnMiss, E16 scale).
            new BbChampionModule("Chogath", data),
            // Phase 3, new champions (25 October 2026, batch 239, wave 4): Warwick runs from his shipped scripts (Eternal Thirst, Hungering Strike, Hunter's
            // Call, Blood Scent, Infinite Duress: the Suppression global E03 and the channel E08); a retail bot on the generic Bot scripts (batch 160).
            new BbChampionModule("Warwick", data),
            // Phase 3, new champions (25 October 2026, batch 240, wave 4): Nunu runs from his shipped scripts (E08 Absolute Zero, the channel; a retail bot).
            new BbChampionModule("Nunu", data),
            // Phase 3, new champions (25 October 2026, wave 5, batch 243): Malphite runs from his shipped scripts (no hand-written module ever existed; a retail bot).
            new BbChampionModule("Malphite", data),
            // Phase 3, new champions (25 October 2026, wave 5, batch 245): Trundle runs from his shipped scripts (E02 ghost-proof pillar, E07 nearby-death hook; a retail bot).
            new BbChampionModule("Trundle", data),
            // Phase 3, new champions (25 October 2026, wave 7, batch 247): Nasus runs from his shipped scripts (E01 statuses, E16 model scale; a retail bot).
            new BbChampionModule("Nasus", data),
            // Phase 3, new champions (25 October 2026, wave 6, batch 249): Maokai runs from his shipped scripts (Sap Magic and the saplings are wave 6's
            // bundle; a retail bot, seated through this module and the generic Bot scripts).
            new BbChampionModule("Maokai", data),
            // Phase 3, new champions (25 October 2026, batch 250, wave 8): Renekton runs from his shipped scripts (the fury pool, E16 scale, E21 colour; a retail bot).
            new BbChampionModule("Renekton", data),
            // Phase 3, new champions (26 October 2026, wave 8, batch 251): Akali runs from her shipped scripts (E14 ammo buff, E20 energy pool,
            // the kill and assist hooks; not a retail bot, so the generic bot template applies).
            new BbChampionModule("Akali", data),
            // Phase 3, new champions (25 October 2026, wave 8, batch 255): Corki runs from his shipped scripts (the Missile Barrage ammo of E14 and E29,
            // the Valkyrie trail of the zone buffs; no hand-written module ever existed; not a retail bot).
            new BbChampionModule("Corki", data),
            // Phase 3, new champions (25 October 2026, batch 257, wave 8): Dr. Mundo runs from his shipped scripts (the health-cost pool E20, Burning Agony's
            // AdjustCooldown E13; not a retail bot, so the generic bot template seats him).
            new BbChampionModule("DrMundo", data),
            // Phase 3, new champions (26 October 2026, wave 8, batch 260): Rumble runs from his shipped scripts (the heat pool E20, the Danger Zone and
            // Overheat states, the Equalizer's drag aim E21; not a retail bot, so the generic bot template applies).
            new BbChampionModule("Rumble", data),
            // Phase 3, new champions (26 October 2026, batch 258, waves 7 and 8): Karma runs from her shipped scripts (E14 Mantra charges,
            // E17 Spirit Bond's link and rectangle, E29 stacked adds, E21 spell level; not a retail bot, so the generic bot template applies).
            new BbChampionModule("Karma", data),
            // Phase 3, new champions (25 October 2026, wave 8, batch 261): Vladimir runs from his shipped scripts (the health-cost pool, the
            // Tides of Blood heal hook, Blood Gorged's max health; not a retail bot, so the generic bot template applies).
            new BbChampionModule("Vladimir", data),
            // Phase 3, new champions (25 October 2026, batch 262, wave 4): FiddleSticks runs from his shipped scripts (Drain and Crowstorm are the E08
            // channels, Dark Wind a bouncing crow; not a retail bot, so the generic bot template applies).
            new BbChampionModule("FiddleSticks", data),
            // Phase 3, new champions (26 October 2026, batch 266, wave 4): Katarina runs from her shipped scripts (E08 Death Lotus, the channel;
            // E06 Voracity and E09 Grievous Wound of the earlier waves; not a retail bot, so the generic bot template applies).
            new BbChampionModule("Katarina", data),
            // Phase 3, new champions (26 October 2026, batch 267, wave 4): Swain runs from his shipped scripts (Ravenous Flock's E13 cooldown adjuster,
            // the Carrion Renewal kill hook E06, Decrepify's beam statuses E01; not a retail bot, so the generic bot template applies).
            new BbChampionModule("Swain", data),
            // Phase 3, new champions (26 October 2026, batch 265, wave 4): Gragas runs from his shipped scripts (the channelled Drunken Rage, E08; the Barrel
            // Roll's detonation, the Body Slam dash and the Explosive Cask's knock-back; not a retail bot, so the generic bot template applies).
            new BbChampionModule("Gragas", data),
            // Phase 3, new champions (26 October 2026, batch 268, wave 4): Tristana runs from her shipped scripts (E13 Buster Shot's AdjustCastInfo,
            // E06 Draw a Bead's kill and assist hooks, E09 Grievous Wound; not a retail bot, so the generic bot template applies).
            new BbChampionModule("Tristana", data),
            // Phase 3, new champions (26 October 2026, batch 272, wave 3): Irelia runs from her shipped scripts (Ionian Duelist's enemy count and CC
            // reduction, Bladesurge's dash and kill reset, Hiten Style's heal and true damage, Equilibrium Strike, Transcendent Blades' four charges; not a
            // retail bot, so the generic bot template applies).
            new BbChampionModule("Irelia", data),
            // Phase 3, new champions (26 October 2026, batch 273, wave 3): Jax runs from his shipped scripts (Counter Strike's dodge hook E07,
            // the Equipment Mastery aura of the character script; not a retail bot, so the generic bot template applies).
            new BbChampionModule("Jax", data),
            // Phase 3, new champions (26 October 2026, batch 275, wave 3): Poppy runs from her shipped scripts (the Valiant Fighter damage share, the
            // Paragon stacks on a hit and on being hit (E07), Devastating Blow's bonus, Heroic Charge's push and wall stun, Diplomatic Immunity's
            // target filter; not a retail bot, so the generic bot template applies).
            new BbChampionModule("Poppy", data),
            // Phase 3, new champions (26 October 2026, batch 278, wave 3): Rammus runs from his shipped scripts (Spiked Shell's armour damage, Powerball's
            // roll with the RammusPB form, Defensive Ball Curl's RammusDBC form and damage return, Puncturing Taunt's taunt and armour debuff, Tremors'
            // timed area; not a retail bot, so the generic bot template applies).
            new BbChampionModule("Rammus", data),
            // Phase 3, new champions (26 October 2026, batch 282, wave 3): Sona runs from her shipped scripts (Hymn of Valor, Aria of Perseverance and Song
            // of Discord are toggles that override the basic attack; Power Chord's every-third-spell upgrade; Crescendo's stun; not a retail bot, so the generic template applies).
            new BbChampionModule("Sona", data),
            // Phase 3, new champions (26 October 2026, batch 280, wave 3): Sion runs from his shipped scripts (Feel No Pain's block on
            // basic attacks (E07), Enrage's kill and assist stacks (E06), Cannibalism, Death's Caress and Cryptic Gaze; not a retail bot,
            // so the generic bot template applies).
            new BbChampionModule("Sion", data),
            // Phase 3, new champions (26 October 2026, batch 283, wave 3): Twitch runs from his shipped scripts (Deadly Venom's stacks on a hit (E07),
            // Ambush's stealth break on a launch (E12), Venom Cask, Expunge, Spray and Pray; not a retail bot, so the generic bot template applies).
            new BbChampionModule("Twitch", data),
            // Phase 3, new champions (26 October 2026, batch 285, wave 2): Urgot runs from his shipped scripts (Zaun-Touched Bolt Augmenter
            // on his attacks and Acid Hunter, Acid Hunter and its homing child, Terror Capacitor, Noxian Corrosive Charge with its boom and
            // corrosive debuff, Hyper-Kinetic Position Reverser with its swap and suppression; not a retail bot, so the generic template applies).
            new BbChampionModule("Urgot", data),
            // Phase 3, new champions (26 October 2026, batch 290, wave 2): Kennen runs from his shipped scripts (Mark of the Storm, Thundering
            // Shuriken, Electrical Surge with its empowered attack, Lightning Rush, Slicing Maelstrom; not a retail bot, so the generic template applies).
            new BbChampionModule("Kennen", data),
            // Phase 3, new champions (26 October 2026, batch 289, wave 2): Singed runs from his shipped scripts (Empowered Bulwark, Poison Trail
            // with its applicators and marker, Mega Adhesive with its pool and slow, Fling, Insanity Potion; not a retail bot, so the generic template applies).
            new BbChampionModule("Singed", data),
            // Phase 3, new champions (26 October 2026, batch 294, wave 5): Evelynn runs from her shipped scripts (Hate Spike, Shadow Walk with its
            // stealth and next-hit slow, Ravage, Malice and Spite, and the passive Determined Killer; not a retail bot, so the generic template applies).
            new BbChampionModule("Evelynn", data),
            // Phase 3, new champions (26 October 2026, batch 295, wave 5): Pantheon runs from his shipped scripts (Aegis Protection,
            // Spear Shot, Aegis of Zeonia, Heartseeker Strike, Grand Skyfall; not a retail bot, so the generic template applies).
            new BbChampionModule("Pantheon", data),
            // Phase 3, new champions (26 October 2026, batch 299, wave 6): Galio runs from his shipped scripts (E04 and E18 of wave 6,
            // E08 channel, E10 spell hit; not a retail bot, so the generic template applies).
            new BbChampionModule("Galio", data),
            // Phase 3, new champions (25 October 2026, batch 300, wave 6): Teemo runs from his shipped scripts (Blinding Dart, Move Quick with its
            // passive and speed, Toxic Shot with its applicator, Noxious Trap with its mushrooms, Camouflage; not a retail bot, so the generic template applies).
            new BbChampionModule("Teemo", data),
            // Phase 3, new champions (26 October 2026, batch 304, wave 6): Gangplank runs from his shipped scripts (E01, E04 and E06
            // of wave 6 are on master; not a retail bot, so the generic template applies).
            new BbChampionModule("Gangplank", data),
            // Phase 3, new champions (25 October 2026, batch 308, wave 6): Veigar runs from his shipped scripts (Baleful Strike with its kill
            // ability power, Dark Matter with its sight bubble and delayed area, Event Horizon with its marker ring, Primordial Burst, Equilibrium;
            // not a retail bot, so the generic template applies).
            new BbChampionModule("Veigar", data),
            // Phase 3, new champions (26 October 2026, batch 312, wave 6): Caitlyn runs from her shipped scripts (E02, E04, E08,
            // E11 and E29 of wave 6 are on master; not a retail bot, so the generic template applies).
            new BbChampionModule("Caitlyn", data),
            // Phase 3, new champions (25 October 2026, batch 313, wave 6): Nocturne runs from his shipped scripts (Umbra Blades, Duskbringer and its Dusk
            // Trail, Shroud of Darkness, Unspeakable Horror, Paranoia and its dash; not a retail bot, so the generic bot template applies).
            new BbChampionModule("Nocturne", data),
            // Phase 3, new champions (25 October 2026, batch 317, wave 6): Cassiopeia runs from her shipped scripts (Noxious Blast with its delayed
            // poison area and haste, Miasma with its growing cloud, Twin Fang with its poisoned-target refund, Petrifying Gaze with its front stun;
            // Deadly Cadence is her passive; not a retail bot, so the generic template applies).
            new BbChampionModule("Cassiopeia", data),
            // Phase 3, new champions (25 October 2026, batch 321, wave 6): Talon runs from his shipped scripts (Mercy, Noxian Diplomacy,
            // Rake, Cutthroat, Shadow Assault; E01, E02 and E18 of wave 6; not a retail bot, so the generic template applies).
            new BbChampionModule("Talon", data),
            // Phase 3, new champions (25 October 2026, batch 318, wave 6): Skarner runs from his shipped scripts (Crystal Slash with its
            // energy and slow, Crystalline Exoskeleton, Fracture with its missile, Impale with its stun, suppression and sight bubble;
            // not a retail bot, so the generic template applies).
            new BbChampionModule("Skarner", data),
            // Phase 3, new champions (26 October 2026, batch 319, wave 6): Leona runs from her shipped scripts (E01, E02, E04, E15 and
            // E21 of wave 6 are on master; not a retail bot, so the generic template applies).
            new BbChampionModule("Leona", data),
            // Phase 3, new champions (25 October 2026, wave 7, batch 328): Karthus runs from his shipped scripts (Lay Waste, Wall of Pain, Defile, Requiem
            // and the Death Defied zombie life; E01, E04, E06, E08, E11, E17 and E18 of wave 7 are on master; not a retail bot, so the generic template applies).
            new BbChampionModule("Karthus", data),
            // Phase 3, new champions (25 October 2026, batch 327, wave 7): Olaf runs from his shipped scripts (Undertow with its axe, Berserker Rage,
            // Vicious Strikes, Reckless Swing, Ragnarok; E01, E04, E07 and E16 of wave 7; not a retail bot, so the generic template applies).
            new BbChampionModule("Olaf", data),
            // Phase 3, new champions (26 October 2026, batch 330, wave 7): Anivia runs from her shipped scripts (Flash Frost with its second press and
            // its flight's end, Crystallize's wall of ice blocks, Frostbite, Glacial Storm's toggle, Rebirth's egg; not a retail bot, so the generic
            // template applies).
            new BbChampionModule("Anivia", data),
            // Phase 3, new champions (26 October 2026, batch 326, wave 6): Kog'Maw runs from his shipped scripts (Caustic Spittle, Bio-Arcane
            // Barrage, Void Ooze, Living Artillery; Icathian Surprise is his passive, the zombie life of E11). Not a retail bot, so the generic
            // bot template applies.
            new BbChampionModule("KogMaw", data),
            // Phase 3, new champions (25 October 2026, wave 7, batch 333): Lux runs from her shipped scripts (Light Binding, Prismatic Barrier,
            // Lucent Singularity, Final Spark and the Illumination passive; E01, E17 and E18 of wave 7 are on master; not a retail bot, so the generic template applies).
            new BbChampionModule("Lux", data),
            // Phase 3, new champions (26 October 2026, batch 334, wave 7): Xerath runs from his shipped scripts (Arcanopulse, Locus of Power, Mage Chains,
            // Arcane Barrage, Ascended Form; E01, E02, E03, E04, E17, E18, E24 and E29 of wave 7 are on master; not a retail bot, so the generic template applies).
            new BbChampionModule("Xerath", data),
            // Phase 3, new champions (26 October 2026, wave 7, batch 332): Jarvan IV runs from his shipped scripts (Martial Cadence, Dragon Strike,
            // Golden Aegis, Demacian Standard and Cataclysm; E01, E02, E04 and E17 of wave 7 are on master; not a retail bot, so the generic template applies).
            new BbChampionModule("JarvanIV", data),
            // Phase 3, new champions (26 October 2026, batch 337, wave 9): LeBlanc runs from her shipped scripts (Sigil of Silence and its mark,
            // Distortion and its return, Ethereal Chains, Mimic, Mirror Image's clone through LeblancMI); not a retail bot, so the generic template applies.
            new BbChampionModule("Leblanc", data),
            // Phase 3, new champions (25 October 2026, batch 338, wave 9): Wukong (MonkeyKing) runs from his shipped scripts (Crushing Blow, Decoy,
            // Nimbus Strike, Cyclone; Stone Skin is his passive). Not a retail bot, so the generic bot template applies.
            new BbChampionModule("MonkeyKing", data),
            // Phase 3, new champions (26 October 2026, batch 342, wave 9): Malzahar runs from his shipped scripts (Call of the Void with its two
            // portals, Null Zone, Malefic Visions, Nether Grasp, the Summon Voidling passive with its UncontrollablePet voidlings; not a retail bot,
            // so the generic template applies).
            new BbChampionModule("Malzahar", data),
            // Phase 3, new champions (26 October 2026, batch 339, wave 9): Heimerdinger runs from his shipped scripts (H-28G Evolution Turret with its
            // Minion.lua turrets, Hextech Micro-Rockets, CH-1 Concussion Grenade, UPGRADE!!! and Techmaturgical Repair Bots; E01, E02, E10 and E19 are on
            // master; not a retail bot, so the generic bot template applies).
            new BbChampionModule("Heimerdinger", data),
            // Phase 3, new champions (26 October 2026, batch 341, wave 9): Mordekaiser runs from his shipped scripts (Iron Man's shield pool,
            // Mace of Spades, Creeping Death, Siphon of Destruction, Children of the Grave with its ghost, a clone pet of the E19 family; not
            // a retail bot, so the generic template applies).
            new BbChampionModule("Mordekaiser", data)]);
        var spells = new SpellSystem(world, movement, spellCombat, effects,
            spellBuffs, hooks, dispatcher, data, rng, HeroOrderVisible,
            heroStats: stats);
        spells.ScriptPets = new BbPetServices(clones, boxes); // the spawn natives of the BuildingBlocks engine
        spells.EndZombieLife = hooks.EndZombieLife; // BBForceDead of a unit in a zombie life (Yorick's Omen of Death)
        stats.CastGateOpen = (hero, slot) => spells.ScriptCastGateOpen(hero, slot); // finding 32: Starcall's gate clears its castable bit
        stats.CastsWhileDisabled = (hero, slot) => spells.CastsWhileDisabled(hero, slot);
        var tibbers = new TibbersAiSystem(world, movement, combat, effects,
            minionVision, canSee: HeroOrderVisible, random: rng);
        // Batch 163: the units a BBSpawnMinion gave idle.lua or Aggro.lua run their shipped chunks
        // (Idle.luaobj, Aggro.luaobj) on this host, over the same services the hand-written ladder
        // drove. The buff lifecycle raises their control callbacks on it.
        tibbers.Host.OnError = failure => sessionLog?.Write("GAMEPLAY", "ERROR", "script.exception",
            new Dictionary<string, object?>
            {
                ["script"] = failure.Script, ["hook"] = failure.Hook, ["message"] = failure.Message,
            });
        buffs.Spawned = tibbers.Host;
        // Every pet's shipped AI (Pet.luaobj, UncontrollablePet.luaobj, YorickPHPet.luaobj) runs on
        // one host, over the same services the hand-written pet machine drove. The host raises the
        // control events (taunt, fear, the movement and attack bits) through the buff lifecycle.
        var pets = new PetAiService(world, movement, combat, effects, data,
            minionVision, canSee: HeroOrderVisible, random: rng, buffs: spellBuffs);
        pets.Host.OnError = failure => sessionLog?.Write("GAMEPLAY", "ERROR", "script.exception",
            new Dictionary<string, object?>
            {
                ["script"] = failure.Script, ["hook"] = failure.Hook, ["message"] = failure.Message,
            });
        buffs.Pets = pets.Host;

        hooks.Attach(stats, combat, buffs, buffEffects, effects, damage, passives,
            neutral, scheduler,
            spells);

        var sessionMovement = new MovementSessionAdapter(movement);
        var sessionCombat = new CombatSessionAdapter(combat, world);
        var sessionEffects = new EffectSessionAdapter();
        var shopModel = new ShopService(world, catalog, stats,
            new ProtocolShopTransport(delivery), MapItemRules.Load(data.Paths), items.ApplyPurchasedElixir,
            // A shop mutation rebinds the item copies before the shop publishes (the copies' tooltip rows leave with
            // them); the item poll keeps doing the same for an inventory that changed outside the shop.
            hero => spells.ItemScripts?.SyncInventory(hero));
        var shop = new ProductionShopService(shopModel);
        var speed = new ProductionSimulationSpeed();
        var cheats = new ProductionCheatService(combat);
        var chat = new ProductionChatService(combat, effects, speed, world, stats, navigation, data, playtest);
        var session = new DuelSession(new SessionDependencies(stats, sync,
            sessionMovement, sessionCombat, sessionEffects, shop, cheats, chat) { MapId = mapId }, world, data,
            orderChampion, solo ? orderChampion : chaosChampion,
            originalRosterCounts,
            matchCipher, readySettleSeconds,
            solo: solo,
            visibilityBaseline: visibilityBaseline,
            minionFidelity: minionFidelity,
            combatClock: () => combat.CurrentTime,
            combatGameOver: () => combat.GameOver,
            combatEndGameSent: () => combat.EndGameSent,
            combatShopClosed: () => combat.ShopClosed,
            roster: roster,
            soloPlayerId: soloPlayerId);
        session.GroundHeight = point => navigation.HeightAt(point);
        holder.Session = session;
        // NOT RETAIL: production safety (the same contract as BbHookRouter.Fail). A script that raises loses that one
        // hook run and writes an ERROR record; the match keeps going.
        void LogScriptException(BbScriptFailure failure) => sessionLog?.Write("GAMEPLAY", "ERROR", "script.exception",
            new Dictionary<string, object?>
            {
                ["script"] = failure.Script, ["hook"] = failure.Hook, ["block"] = failure.BlockPath,
                ["message"] = failure.Message,
            });
        // The mastery area (batch 140): every hero's allocated talent scripts run their character hooks — the ten
        // hand-written champions included, because a mastery page is not a champion script. Installed for every match.
        hooks.LoadoutHooks = new BbLoadoutHooks(() => buffs, LogScriptException);
        var scriptHooks = new BbHookFanout();
        // The summoner spells (batch 142 step 2): every shipped Summoner*.luaobj runs from its own code, bound to
        // (hero, summoner slot) on the hero's loadout owner. Its sink has its own slot on the gameplay hooks —
        // MatchGameplayHooks.SummonerHooks, next to LoadoutHooks — because a summoner spell is not a champion script:
        // every hero of every champion casts them, the ten hand-written champions included, and a match that has not
        // created a hero yet still runs them. The champion sink keeps its own slot and stays empty until a
        // script-driven hero is created (the rule scripting.bb-annie-* pins). NOT RETAIL: retail's summoner scripts run
        // in the game server's own VM for every seat.
        var summonerScripts = new BbSummonerModule(data);
        spells.LoadoutHooks = hooks.LoadoutHooks;
        spells.SummonerScripts = summonerScripts;
        holder.Spells = spells;
        // Batch 419 (Reconstruction): an effect the wire sends unbound at a dead unit ends with that unit's corpse (SpellSystem.EndEffectWithUnitCorpse).
        delivery.UnboundEffectAtDeadUnit += spells.EndEffectWithUnitCorpse;
        hooks.SummonerHooks = summonerScripts.Bind(spells, hooks.LoadoutHooks, LogScriptException);
        // Batch 144 step 1: the item copies' own scripts. Its sink has its own slot next to the summoner spells' for
        // the same reason: an item is not a champion script, every hero of every champion buys them, and the ten
        // hand-written champions do too. The mastery page's live buffs (Ardor) get their own host through BindMatch.
        var itemScripts = new BbItemModule(data);
        spells.ItemScripts = itemScripts;
        hooks.ItemHooks = itemScripts.Bind(spells, hooks.LoadoutHooks, LogScriptException);
        // Batch 159 step 2: the non-champion units' own character scripts (the turrets' CharScript*.luaobj), bound one
        // unit at a time when the unit is created. Its sink has its own slot for the same reason the items' has one: a
        // unit character script is not a champion script and no hero owns the unit. Installed for every match; a character
        // with no shipped script is never bound and keeps its hand-written rules. Batch 173: the seam lives in
        // MatchScriptSeams so the replay harnesses install the same one instead of their own copy.
        var (unitScripts, unitHooks) = MatchScriptSeams.InstallUnitCharacters(spells, data, LogScriptException);
        hooks.UnitHooks = unitHooks;
        // The active slot cast of an item is the shipped Spells/<SpellName>.luaobj's own (batch 149 steps 3 and 4):
        // ItemService keeps the inventory, the used slot's cooldown and the wire, and the script owns what the use
        // does. The items are built before the spell system, so the use path reaches the script through this hook;
        // the stealth flag is the same script's own. Zhonya's BBStopChanneling and BBDestroyMissileForTarget need no
        // hook of their own any more: both natives reach the spell system and the combat service from their own
        // handlers (BbNativeHandlers).
        items.CanCastItemSpell = (hero, spellName, inventorySlot) =>
            itemScripts.CanCastItemSpell(hero, spellName, inventorySlot);
        items.CastItemSpell = (hero, spellName, inventorySlot, position, end, target, itemId) =>
            itemScripts.CastItemSpell(hero, spellName, inventorySlot, position, end, target, itemId);
        items.CastingBreaksStealthOf = (hero, spellName) => itemScripts.CastingBreaksStealth(spellName);
        hooks.LoadoutScriptHooks = hooks.LoadoutHooks.BindMatch(spells, LogScriptException);
        // A map unit gets its character script at the character's creation, the rule HeroCreated gives a hero
        // (batch 159 step 2). CreateMapEntities runs the turret's CharOnActivate through here and, told that the
        // scripts own the unit, leaves the hand-written turret bonuses to them. The wave and jungle schedulers take
        // the same bind (batch 159 part 2): a lane minion and a camp monster are map units too, and the siege/super
        // and monster scripts own the buffs and the hit scalars the hand-written rules used to stand in for.
        MatchScriptSeams.BindUnitCharacters(session, unitScripts);
        MatchScriptSeams.BindUnitCharacters(waves, unitScripts);
        MatchScriptSeams.BindUnitCharacters(scheduler, unitScripts);
        session.HeroCreated = hero =>
        {
            // Batch 556, Reconstruction: a spawn slot on a blocked cell starts on the nearest floor cell. Only Map4's fountain has one: the
            // shipped HeroSpawnOffsets.inibin Order5 slot 3 (-42.8, -168) falls on its southern pillar. Retail Twisted Treeline seats three a
            // side, whose slots all land on floor (map4.hero-spawns-on-floor), and the client puts an actor standing on a blocked cell back
            // on floor at its next stop (the client's stop-placement routine); a slot on floor keeps its exact point. Four cells at most: a point further from floor
            // is no fountain slot (a check composing another map than the process's places its heroes at the process map's spawns).
            if (navigation.NearestMovementFloor(hero.Position, maxCells: 4) is { } floor) hero.Position = floor;
            // The mastery page is bound when the character is created, not on the first damage it takes: its live buffs
            // (Ardor) are part of the hero's stats from the first refresh, the way SetVarsByLevel runs at creation.
            hooks.LoadoutHooks?.OwnerFor(hero);
            if (dispatcher.FindModule(hero.ChampionName) is BbChampionModule scripted)
            {
                // The first hero of a script-driven champion binds its module to this match and installs the sink
                // (NOT RETAIL: retail's scripts run in the game server's own VM); every hero then gets SetVarsByLevel and
                // CharOnActivate, which retail runs when the character is created.
                if (!scripted.IsBound)
                {
                    scriptHooks.Add(scripted.Bind(spells, LogScriptException));
                    hooks.ScriptHooks = scriptHooks;
                }
                scripted.ActivateHero(hero);
            }
            // Batch 159 part 3: the team-size catch-up controller (Spells/ChampionChampionDelta.luaobj). Every champion character
            // script adds it in CharOnActivate, which the block above just ran for a script-bound champion. The ten hand-written
            // champions never run a character script, and one script-bound champion's own script omits the add
            // (CharScriptOrianna has no ChampionChampionDelta block), so the add is made through the match's unit-character module
            // whenever the hero does not already carry the row its script put there. BUFF_REPLACE_EXISTING is the script's own add
            // type, so the two paths cannot stack: one live instance, its periodic and its ramp, for every hero.
            if (!hero.Buffs.Any(buff => buff.Name == TeamSizeCatchUp.ControllerBuff))
                unitScripts.AddShippedBuff(hero, TeamSizeCatchUp.ControllerBuff, BuffType.Internal,
                    TeamSizeCatchUp.ControllerSeconds);
            // Batch 203: Fortify's tower bonus. APBonusDamageToTowers is a champion's own CharOnActivate add, and a hero whose champion has no
            // module never runs one. Reconstruction: such a hero gets that shipped add here and nothing else of its champion's script. The
            // twelve bot champions without a module (Ashe, Chogath, Malphite, Maokai, MasterYi, MissFortune, Nasus, Nunu, Renekton, Shen,
            // Trundle, Warwick) each add it in their own shipped CharOnActivate, so this restores the same buff for them.
            if (dispatcher.FindModule(hero.ChampionName) is null)
                unitScripts.AddShippedBuff(hero, "APBonusDamageToTowers", BuffType.Internal, 25000.0);
        };
        session.ChampionCastTargetsUnit = spells.TargetsUnit;
        session.OtherCastTargetsUnit = (hero, summoner, slot) =>
            summoner ? spells.SummonerTargetsUnit(hero, slot) :
            slot == SpellSystem.RecallSlot ? false : ItemService.InputTargetsUnit(hero, slot, data, catalog);
        session.CurrentTargetVisible = HeroOrderVisible;
        session.PrivatePacketSink = (observerId, packet) =>
            delivery.EnqueueForObserver(observerId, packet);

        bool TeamSight(Team team, Entity other) =>
            VisibilityRules.RevealsEntity(world, team, environment, TeamVision(team),
                point => holder.ItemVision.RevealsStealthAt(team, point), other);
        bool HeroOrderVisible(Entity hero, Entity other) => TeamSight(hero.Team, other);
        movement.HeroPlanVisibility = HeroOrderVisible;
        // Batch 163: Hero.luaobj answers OnTauntBegin/OnTauntEnd and OnFearBegin/OnFearEnd itself (its own state machine
        // takes the taunt target and runs the fear wander). Batch 173: the pair lives in MatchScriptSeams so the replay
        // harnesses attach the same one.
        var heroOrders = MatchScriptSeams.AttachHeroOrders(spells, buffs, world, movement, combat, effects,
            HeroOrderVisible, spellBuffs, data, rng, fidelityTrace);
        heroOrders.NoteWaypointIntent = (hero, route, reason) =>
            collisionTrace.NoteWaypointIntent(hero.NetId, reason, world.Clock.SyncId(), route);
        // DIAGNOSTICS: tool-only (Amberline.Tools match). Retail runs the bot scripts only on bot seats;
        // a headless match runs them on all ten connected seats (see BotController.Controls and
        // DuelSession.AfkTrackingDisabled).
        session.AfkTrackingDisabled = botsControlAllSeats;
        var bots = new BotController(world, movement, combat, spells, shopModel, catalog,
            heroOrders, waves, effects, session.Participants, HeroOrderVisible, rng,
            controlsAllSeats: botsControlAllSeats, data: data);
        // Bot.luaobj and the Task*.luaobj modules it loads run on the same Lua AI host as
        // Turret.luaobj, so a raising bot script is logged the same way (Part 3's no
        // script.exception criterion depends on this being wired at all).
        bots.Host.OnError = failure => sessionLog?.Write("GAMEPLAY", "ERROR", "script.exception",
            new Dictionary<string, object?>
            {
                ["script"] = failure.Script, ["hook"] = failure.Hook, ["message"] = failure.Message,
            });
        var gameplay = new ProductionGameplayDispatcher(movement, combat, spells,
            stats, items, world, effects, pets, heroOrders, HeroOrderVisible, fidelityTrace, collisionTrace, playtest, mapId);
        // Batch 563: the Basic Tutorial (map 3). Its TutorialScript's client packets and unit flags, the BuildingBlocks natives its
        // BBLuaConversionLibrary wrappers call (run by the unit-character module, the host of the map's own units), and the client's
        // two acknowledgements (popup closed, audio finished).
        if (mapId == GameMaps.Tutorial)
        {
            var tutorial = new TutorialMatchEngine(delivery, () => combat.CurrentTime, sessionLog,
                hero => session.Participants.FirstOrDefault(seat => ReferenceEquals(seat.Hero, hero))?.ClientId ?? 0);
            level.TutorialEngine = tutorial.Handle;
            level.RunBuildingBlock = unitScripts.RunLevelBlock;
            level.TutorialLog = (message, properties) => sessionLog?.Write("GAMEPLAY", "INFO", message, properties);
            // Batch 576: an expired unit that outlived its ExpirationTimer dies the ordinary death a pet's end takes (no one is credited).
            level.RetireUnit = unit => combat.Kill(unit);
            // Batch 580: CreateItem's free grant, and the tutorial bots' scripted order (LevelScriptHost.DirectTutorialBots, Reconstruction).
            level.GrantItem = (hero, item) => shopModel.Grant(hero, item) is { Accepted: false } refused ? refused.Reason : null;
            level.TutorialBotAttack = (hero, target) =>
            {
                if (session.Participants.FirstOrDefault(seat => ReferenceEquals(seat.Hero, hero)) is { } seat) bots.SetSeatScripted(seat.PlayerId, true);
                heroOrders.StartAttack(hero, target);
            };
            // Batch 593: a script's minion.lua unit walks the team's lane route from its nearest node (LevelScriptHost.ScriptMinionRoute).
            level.ScriptMinionRoute = unit =>
            {
                if (unit.Team is not (Team.Order or Team.Chaos)) return null;
                List<Position2>? ahead = null;
                var nearest = double.PositiveInfinity;
                foreach (var lane in MapDefinitions.LanePaths.Keys)
                {
                    var route = waves.LaneRoute(unit.Team, lane);
                    for (var node = 0; node < route.Count; node++)
                    {
                        var (dx, dz) = (route[node].X - unit.Position.X, route[node].Z - unit.Position.Z);
                        if (dx * dx + dz * dz >= nearest) continue;
                        nearest = dx * dx + dz * dz;
                        ahead = route.Skip(node).ToList();
                    }
                }
                return ahead;
            };
            session.TutorialAcknowledgement = payload =>
            {
                if (payload[0] == TutorialPackets.PopupClosedClientOpcode) level.TutorialPopupClosed(combat.CurrentTime);
                else if (payload[0] == TutorialPackets.ShopOpenedClientOpcode) level.TutorialShopOpened(combat.CurrentTime);
                else if (TutorialPackets.CameraFocus(payload, point => navigation.HeightAt(point)) is { } focus) level.TutorialCameraMoved(focus);
                else if (TutorialPackets.AudioEventFinishedId(payload) is uint id) level.TutorialAudioFinished(id, combat.CurrentTime);
            };
        }
        // Map 8 starts its enabled quests (OdinScript.xml); every other map gets null here and runs no behaviour trees.
        // Batch 446: an encounter's units spawn through the unit-character module (BBSpawnMinion's path, with the character's CharOnActivate).
        var quests = BehaviorTreeQuestHost.ForMap(mapId, data.Paths, world,
            (key, detail) => sessionLog?.Write("AI", "INFO", "behaviour_tree.note",
                new Dictionary<string, object?> { ["key"] = key, ["detail"] = detail }),
            spawnUnit: (skin, position, team) => unitScripts.SpawnEncounterUnit(skin, position, team),
            // Batch 451: a wave's units are minions (the capture trees count and kill minions by unit type).
            spawnMinion: (skin, position, team) => unitScripts.SpawnEncounterUnit(skin, position, team, UnitType.Minion),
            // Batch 451: a wave unit routes on the map's grid and runs its script on the lane engine (OdinWaves).
            navigation: navigation, minions: minions,
            // Batch 471: the quest points are drawn from the match's random source.
            random: rng,
            // Batch 448: the capture leaves' hooks. Gold and kills take the paths the kills and the scripts use. Batch 455: the
            // announcements (0x4A, global) go through the same Send hook as the capture packets.
            hooks: new CapturePointHooks(
                Send: combatTransport.SendDominion,
                Kill: (unit, killer) => combat.Kill(unit, killer),
                AwardGold: (hero, gold) => combat.AwardHeroGold(hero, gold, null),
                // Batch 505: a capture or a neutralise costs the opposing nexus, down to the floor.
                DamageNexusAboveFloor: (team, amount, showLoss) => combat.DamageDominionNexusAboveFloor(team, amount, showLoss),
                // Batch 517 (Reconstruction): a champion counts on its point once its capture channel has charged (1.5 s), not from the click.
                CaptureChannelTarget: hero => spells.ChargedCaptureTarget(hero),
                // Batch 475b: a channel aimed at a replaced guardian continues on the replacement.
                RetargetChannels: (from, to, team) => spells.RetargetCaptureChannels(from, to, team),
                // Batch 479h (Reconstruction): the replaced guardian's suppression and team stacks go to its replacement.
                CarryBuffs: (replacement, carried) => spells.CarryGuardianBuffs(replacement, carried),
                // Batch 480b: the capturing hero of a point (the display source of its score damage).
                CapturerOf: (guardian, team) => spells.CapturerOf(guardian, team),
                // Batch 493 (Reconstruction): a replaced guardian's capture ends and its buffs end (their deactivation kills their particles) before it leaves.
                EndGuardian: (guardian, owner) => spells.EndReplacedGuardian(guardian, owner),
                // Batch 493 (Reconstruction): the replaced guardian leaves the clients that see it (the leave a queued 0x54 never got).
                Depart: delivery.Depart,
                // Batch 462: the point turrets' render and targetability blocks go out through Send (global) with the match's sync ids.
                SyncId: sync.Next,
                // Batch 501: an owned guardian runs the shipped tower tree (OdinTowerBehavior); its leaves see by the team's fog, keep batch
                // 466's attackable rule (an enemy, targetable to it, not a ward), cast the guardian's own slots through its character module
                // and show the target line (batch 481).
                Tower: new TowerHooks(
                    Sees: HeroOrderVisible,
                    Attackable: (guardian, target) => WorldState.IsEnemy(guardian, target) && CombatService.IsAutoAcquirable(target) &&
                        combat.IsTargetable(target, guardian),
                    CastRange: (unit, slot) => unitScripts.SlotSpellName(unit, slot) is { } name ? data.Spell(name).CastRangeAt(1) : null,
                    Cast: unitScripts.CastSpellSlot,
                    TargetLine: combat.PublishTargetLine),
                // Batch 471: a team's quest tracker packets and its quest announcement go to that team's clients only.
                SendTeam: delivery.EnqueueForTeam));
        // Batch 447: Dominion's score (DualUpdateScoreMath) ticks with the quests; its control counts are the capture points' owners.
        quests?.AttachScore(quests, new DominionScoreSink(combat, combatTransport));
        // Batch 505, 531: a champion death's nexus loss floats over the dying champion, a capture's over the losing team's own champions
        // (0x1C, DominionScoreSink.ShowNexusLoss).
        if (quests is not null) combat.DominionNexusLost = (team, lost, dying) => DominionScoreSink.ShowNexusLoss(delivery, team, lost, dying);
        // Batch 518: the personal score hears the credited kills and the shipped score markers (DominionPlayerScore).
        if (quests is not null)
        {
            combat.DominionPlayerScore = quests.PlayerScore;
            spells.ScriptBuffAdded = quests.PlayerScore.OnScriptBuff;
        }
        // Batch 523: the stairs' lasers are per-viewer, persistent effects (green at your own base, red at the enemy's).
        if (quests is not null) quests.Replicate = delivery.Enqueue;
        // Batch 472: a returning client's point records wait for its first sight of the point's guardian.
        if (quests is not null)
            delivery.FirstSightStateProvider = observer => quests.FirstSightState(observer.HeroNetId, observer.View.Known);
        // Batch 463 (NOT RETAIL, map 8 only): the bots take the capture points through the player's capture order (BotController.Dominion.cs).
        bots.Quests = quests;
        bots.CaptureOrder = gameplay.OrderCapture;
        var simulation = new ProductionSimulationDriver(world, delivery, movement, combat,
            spells, buffs, neutral, scheduler, items, minions, waves, tibbers,
            pets, boxes, shopModel, passives, heroOrders, bots, fidelityTrace,
            visibilityBaseline, minionFidelity, collisionTrace)
        {
            ApplyDeferredOrders = () => { gameplay.PlayDueEmotes(); gameplay.ApplyQueuedWindupOrders(); gameplay.StartDueCaptures(); },
            PlaytestDiagnostics = playtest,
            Quests = quests,
        };
        var dependencies = new MatchRuntimeDependencies(
            hooks, hooks, hooks, dispatcher, hooks.BuffServices, rng,
            replication, environment, simulation,
            team => itemVision.Providers(team),
            (team, point) => itemVision.RevealsStealthAt(team, point),
            entity => entity.Created,
            shop, cheats, chat,
            gameplay.Dispatch);
        var graph = new ProductionMatchGraph
        {
            PlaytestDiagnostics = playtest,
            Navigation = navigation, Dependencies = dependencies, GameData = data,
            World = world, Lifecycle = lifecycle, SyncIds = sync,
            Cipher = matchCipher, Movement = movement,
            Delivery = delivery, HeroStats = stats, CombatTransport = combatTransport,
            Combat = combat, Effects = effects, Buffs = buffs,
            SpellCombat = spellCombat, SpellBuffs = spellBuffs, Spells = spells,
            Bots = bots,
            Session = session, SessionMovement = sessionMovement,
            SessionCombat = sessionCombat, SessionEffects = sessionEffects,
            Tibbers = tibbers, Clones = clones, Pets = pets,
            Minions = minions, Waves = waves,
            SimulationSpeed = speed,
            MovementTrace = collisionTrace, FidelityTrace = fidelityTrace,
        };
        return new MatchRuntime(graph);
    }
}
