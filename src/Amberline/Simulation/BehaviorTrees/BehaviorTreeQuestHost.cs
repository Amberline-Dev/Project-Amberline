using System.Globalization;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Encounters;
using Amberline.Simulation.Minions;

namespace Amberline.Simulation.BehaviorTrees;

/// <summary>
/// The Dominion quest host (map 8): it starts the enabled quest instances of the map's <c>OdinScript.xml</c> and ticks them once
/// per simulation step. Maps 1 and 2 run no behaviour trees, so <see cref="ForMap"/> returns null for them.
/// </summary>
public sealed partial class BehaviorTreeQuestHost : IDominionControl
{
    private readonly List<BehaviorTreeInstance> _instances = new();
    private readonly List<BehaviorTreeInstance> _captures = new();
    private readonly Dictionary<(Team Team, int Position), Entity> _turrets;
    private readonly Dictionary<int, Entity> _guardians;
    private bool _capturesStarted;
    private bool _relicsStarted;
    // Batch 480b: each team's last capturer (its net id, 0 when none): the display source of the score's nexus damage.
    private readonly Dictionary<Team, uint> _lastCapturerNetIds = new();

    /// <summary>Batch 451: the map's navigation grid, for a wave unit's route; null in a host without a minion AI (a check's harness).</summary>
    public NavigationGrid? Navigation { get; init; }

    /// <summary>Batch 451: the lane engine that runs a wave unit's shipped script (MinionOdin or MinionReverse); null in a harness.</summary>
    public MinionAiSystem? Minions { get; init; }

    private BehaviorTreeQuestHost(BehaviorTreeLibrary library, BehaviorTreeContext context,
        Dictionary<(Team Team, int Position), Entity> turrets, Dictionary<int, Entity> guardians)
    {
        Library = library;
        Context = context;
        _turrets = turrets;
        _guardians = guardians;
        foreach (var quest in library.QuestInstances.Where(quest => quest.Enabled))
        {
            _instances.Add(new BehaviorTreeInstance(library, quest.TreeName, context));
            _questNames.Add(quest.Name);
        }
        // Batch 518: the personal score, for the capture leaves (Context.PlayerScore), the kills and the script markers (the match's wiring).
        PlayerScore = new DominionPlayerScore(context.World, PointOwnerAt, context.Capture?.Send);
        context.PlayerScore = PlayerScore;
    }

    /// <summary>Batch 518: the Dominion personal score (<see cref="DominionPlayerScore"/>).</summary>
    public DominionPlayerScore PlayerScore { get; }

    /// <summary>
    /// Batch 518 (Reconstruction: "on a point" is the capture radius): the owner of the capture point within <see cref="CaptureRadius"/> of
    /// <paramref name="at"/> (<see cref="Team.Neutral"/> while neutral), or null when the position is on no point.
    /// </summary>
    public Team? PointOwnerAt(Position2 at)
    {
        foreach (var capture in _captures)
            if (capture.Variable("CapturePosition") is Position3 point &&
                Math.Pow(point.X - at.X, 2) + Math.Pow(point.Z - at.Z, 2) <= CaptureRadius * CaptureRadius)
                return capture.Variable("CurrentPointOwner") is Team owner ? owner : Team.Neutral;
        return null;
    }

    private readonly List<string> _questNames = new();

    /// <summary>Batch 460: whether the started BTInstance of this name still ticks (<c>SetBTInstanceStatus</c> can switch it off).</summary>
    public bool IsRunning(string questName) =>
        _questNames.Contains(questName) && Context.InstanceEnabled.GetValueOrDefault(questName, true);

    public BehaviorTreeLibrary Library { get; }
    public BehaviorTreeContext Context { get; }

    /// <summary>The started quest instances, in the order of <c>OdinScript.xml</c>.</summary>
    public IReadOnlyList<BehaviorTreeInstance> Instances => _instances;

    /// <summary>Batch 446: the capture point instances (EvaluateCapturePoint, CapturePointID 1 to 5), empty until the first step.</summary>
    public IReadOnlyList<BehaviorTreeInstance> Captures => _captures;

    /// <summary>
    /// Batch 448: each capture point's owner (CapturePointID to team), for the score loop. A point with no owner yet is neutral.
    /// </summary>
    public IReadOnlyDictionary<int, Team> CapturePointOwners =>
        _captures.Select((capture, index) => new KeyValuePair<int, Team>(index + 1,
            capture.Variable("CurrentPointOwner") is Team owner ? owner : Team.Neutral)).ToDictionary(pair => pair.Key, pair => pair.Value);

    /// <summary>The score's control count: the capture points the team holds (<see cref="CapturePointOwners"/>).</summary>
    public int ControlCount(Team team) => _captures.Count(capture => capture.Variable("CurrentPointOwner") is Team owner && owner == team);

    /// <summary>
    /// Batch 480b (Reconstruction): the net id of the hero that captured the team's last point (set when the point changes owner, see
    /// <see cref="GuardianTakesOwner"/>); 0 when no hero's channel took one. The display source of the score's nexus damage (DominionScore).
    /// </summary>
    public uint LastCapturerNetId(Team team) => _lastCapturerNetIds.GetValueOrDefault(team);

    /// <summary>
    /// The host of a map: the Dominion host for map 8, null for every other map. <paramref name="turret"/> answers GetTurret instead of the
    /// point turrets the host spawns (a check's seam); the default answers the point turrets.
    /// </summary>
    /// <param name="spawnUnit">Batch 446: spawns an encounter's unit of a character record (the match's BuildingBlocks spawn path); null names none.</param>
    /// <param name="hooks">Batch 448: the engine hooks of the capture leaves (packets, kills, gold, announcements); null names none.</param>
    public static BehaviorTreeQuestHost? ForMap(int mapId, GameDataPaths paths, WorldState world,
        Action<string, string>? sink = null, Func<Team, int, int, Entity?>? turret = null,
        Func<string, Position2, Team, Entity?>? spawnUnit = null, CapturePointHooks? hooks = null,
        Func<string, Position2, Team, Entity?>? spawnMinion = null, NavigationGrid? navigation = null, MinionAiSystem? minions = null,
        IRandomSource? random = null)
    {
        if (mapId != GameMaps.CrystalScar) return null;
        var map = paths.ForMap(GameMaps.CrystalScar);
        var locations = new NamedLocations(map);
        var turrets = new Dictionary<(Team Team, int Position), Entity>();
        var guardians = new Dictionary<int, Entity>();
        var context = new BehaviorTreeContext
        {
            World = world,
            NamedLocation = locations.Locate,
            // Batch 448: GetTurret(team, lane 0, position) answers the point turret of that team (position 2 to 6 is point A to E).
            Turret = turret ?? ((team, lane, position) =>
                lane == 0 && turrets.GetValueOrDefault((team, position)) is { } found ? found : null),
            PointGuardian = guardians.GetValueOrDefault,
            Sink = sink,
            Encounters = new EncounterService(EncounterCatalog.Load(map), spawnUnit, spawnMinion),
            Capture = hooks,
        };
        return new BehaviorTreeQuestHost(BehaviorTreeLoader.LoadMap(map), context, turrets, guardians)
        {
            Navigation = navigation,
            Minions = minions,
            Random = random,
        };
    }

    /// <summary>Batch 447: the Dominion score, once <see cref="AttachScore"/> has run (null until then).</summary>
    public DominionScore? Score { get; private set; }

    /// <summary>Batch 447: starts the Dominion score (DualUpdateScoreMath) on this map, with its control counts and its sink.</summary>
    public DominionScore AttachScore(IDominionControl control, IDominionScoreSink sink) =>
        Score = new DominionScore(Library, Context, control, sink);

    /// <summary>
    /// Reconstruction (batches 477d, 483 and 491): the shatter of the stairs and the crystal prop once a team's nexus falls (the
    /// crystal's gem is killed and it explodes after). Batch 497: the match calls this every step, after combat and also once the game is
    /// over, because the fall is what ends the game: inside <see cref="Tick"/>, which stops at the game over, these clips never went out.
    /// Map 8's scene has no <c>HQ_T1</c>/<c>HQ_T2</c>, so the client has no obj_HQ whose building-die handler plays a
    /// nexus death; the crystal prop's Death and Explode (their AnimEffects: cracked gem, crack lights, Odin_CrystalExplosion_*) are it.
    /// </summary>
    public void TickNexusFall(double now)
    {
        // Batch 523: the fallen team's lasers and lit cores leave every client that holds them (and no later viewer gets them).
        if (Replicate is { } replicate)
            foreach (var removal in _beams.Removals(DominionProps.FromEnvironment(), NexusFallen))
                replicate(removal);
        if (Context.Capture?.Send is not { } send) return;
        foreach (var packet in _nexusDeath.Due(now, DominionProps.FromEnvironment(), NexusFallen))
            send(packet);
    }

    /// <summary>
    /// Batch 523: the match's replication queue (ReplicationDelivery.Enqueue), for the stairs' lasers: per-viewer, persistent effects. Null in a
    /// harness, which then sends no lasers.
    /// </summary>
    public Action<Amberline.Network.Replication.ReplicationMessage>? Replicate { get; set; }

    private bool NexusFallen(Team team) => Context.World.Entities.Values.Any(
        entity => entity.Kind == EntityKind.Nexus && entity.Team == team && !entity.Alive);

    /// <summary>Ticks every started quest instance, then every capture point, then the score, once for this simulation step.</summary>
    public void Tick(double now, double dt)
    {
        if (!_capturesStarted) StartCapturePoints();
        TraceCaptures(now); // DIAGNOSTICS: batch 478 trace (env AMBERLINE_CAPTURE_TRACE); reads only.
        if (!_relicsStarted) StartRelicsAndShrines();
        ServeRelics(now);
        ServeRespawns();
        for (var index = 0; index < _instances.Count; index++)
            if (IsRunning(_questNames[index])) _instances[index].Tick(now, dt);
        HoldLateChampions(now);
        UpdateOpeningBarriers();
        // Batch 477b (Reconstruction): the stairs' closing clips (DominionProps; on the Crystal Scar). Each stage goes out once,
        // at its start, so the four end at the opening release (OpeningCeremonyTiming.ReleaseSeconds).
        if (Context.Capture?.Send is { } sendPacket)
        {
            foreach (var packet in _stairs.Due(now, DominionProps.FromEnvironment(), OpeningCeremonyTiming.ReleaseSeconds))
                sendPacket(packet);
            // Batch 507 (Reconstruction): each stage's two lasers into the crystal and its lit core, at the stage's latch frame. Batch 523:
            // through the delivery, green to the base's own team and red to the other (persistent, so late and returning clients get them).
            if (Navigation is { } grid && Replicate is { } replicate)
                foreach (var beam in _beams.Due(now, DominionProps.FromEnvironment(), OpeningCeremonyTiming.ReleaseSeconds,
                             new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ), NexusFallen))
                    replicate(beam);
        }
        // NOT RETAIL (owner, 29 October 2026): the first start message ("the battle begins in 30 seconds") goes out 30 s before the release.
        if (!_firstAnnouncementSent && now >= OpeningCeremonyTiming.FirstAnnouncementSeconds && Context.Capture?.Send is { } announce)
        {
            _firstAnnouncementSent = true;
            BehaviorLeaves.SendOpeningAnnouncement(announce, CombatPackets.StartGameMessage1);
        }
        // Reconstruction (batches 477d and 483): the release's Raise and Raised_Idle on the stairs (the Odin_SoG prop carries the nexus clips).
        if (Context.Capture?.Send is { } stairSend)
            foreach (var packet in _stairRaiser.Due(now, DominionProps.FromEnvironment(), OpeningCeremonyTiming.ReleaseSeconds))
                stairSend(packet);
        SpawnWaves(now);
        for (var index = 0; index < _captures.Count; index++)
        {
            var capture = _captures[index];
            capture.Tick(now, dt);
            // Reconstruction: the caller hands the tree's NewPointOwner back as the next tick's CurrentPointOwner (the shipped trees name no caller).
            if (capture.Variable("NewPointOwner") is not Team owner) continue;
            if (capture.Variable("CurrentPointOwner") is not Team held || held != owner) GuardianTakesOwner(index + 2, capture, owner);
            capture.SetVariable("CurrentPointOwner", owner);
        }
        ServeQuests(now);
        GuardiansAttack(now, dt);
        Score?.Tick(now, dt);
        PlayerScore.Tick(now);
    }

    // Batch 472: the points whose records a returning client is owed until it holds their guardians, by its hero's net id.
    private readonly Dictionary<uint, HashSet<int>> _owedPoints = new();

    /// <summary>
    /// Batch 472 (Reconstruction: retail's reconnect burst is not recovered). The live Dominion state the server sends only when it changes,
    /// for a client that returns to the match (MatchRuntimeNetwork.BuildReconnectState, after the delivery flush that created what it sees):
    /// the records of each point whose guardian it holds (<see cref="PointRecords"/>), both 0xDD meters as last sent, and its team's open
    /// quests (the 0x90 adds; the announcement is not repeated). A point whose guardian it does not hold yet (an owned guardian gives sight to
    /// its own team only) is owed until the guardian's first sight (<see cref="FirstSightState"/>): its records name the guardian, and the
    /// client applies a 0xDB or 0xDC only to a unit it holds.
    /// </summary>
    public IReadOnlyList<PlaintextPacket> ReconnectState(uint heroNetId, Team team, IReadOnlySet<uint> known)
    {
        var packets = new List<PlaintextPacket>();
        var owed = _owedPoints[heroNetId] = [];
        for (var point = 1; point <= CapturePointNames.Length; point++)
        {
            if (Context.PointGuardian(point + 1) is not { } guardian) continue;
            if (known.Contains(guardian.NetId)) packets.AddRange(PointRecords(point, guardian));
            else owed.Add(point);
        }
        if (Score is { } score)
            foreach (var (meterTeam, health) in score.SentScores) packets.Add(DominionPackets.SetGameScore(meterTeam, health));
        if (_questSet is { } open)
            foreach (var side in open.Where(side => side.Team == team)) packets.AddRange(QuestAdds(side));
        // Batch 518: the personal scores, before the reconnect's 0x4B rows (DuelSession.BuildReconnectMatchState) restate the stats.
        packets.AddRange(PlayerScore.ReplayFor(known, heroNetId));
        return packets;
    }

    /// <summary>Batch 472: the owed points' records, once their guardians exist on the returning client (the delivery flush's provider).</summary>
    public IReadOnlyList<PlaintextPacket> FirstSightState(uint heroNetId, IReadOnlySet<uint> known)
    {
        if (!_owedPoints.TryGetValue(heroNetId, out var owed) || owed.Count == 0) return [];
        var packets = new List<PlaintextPacket>();
        foreach (var point in owed.ToArray())
        {
            if (Context.PointGuardian(point + 1) is not { } guardian || !known.Contains(guardian.NetId)) continue;
            packets.AddRange(PointRecords(point, guardian));
            owed.Remove(point);
        }
        return packets;
    }

    // A point's records as the clients that stayed hold them: its respawn point (graveyard point - 1, BehaviorLeaves.Relics: the 0xDE create
    // once it went out, then the state when one did), then its 0xDB value and 0xDC rate as last sent (BehaviorLeaves.Send).
    private IEnumerable<PlaintextPacket> PointRecords(int point, Entity guardian)
    {
        var graveyard = point - 1;
        if (Context.SentCapture.ContainsKey((DominionPackets.RespawnPointOpcode, guardian.NetId)))
        {
            yield return DominionPackets.CreateRespawnPoint((byte)graveyard, guardian.NetId);
            if (Context.RespawnTeams.TryGetValue(graveyard, out var mask)) yield return DominionPackets.SetRespawnPointState(guardian.NetId, mask);
        }
        foreach (var opcode in new[] { DominionPackets.CapturePointEventOpcode, DominionPackets.CapturePointValueOpcode })
            if (Context.SentCapture.TryGetValue((opcode, guardian.NetId), out var record)) yield return record;
    }

    /// <summary>
    /// The buffs the shipped OdinCaptureChannel puts on its Target, the guardian: OdinGuardianSuppression and, by the channeller's team,
    /// OdinGuardianSuppressionOrder or OdinGuardianSuppressionChaos (ChannelingStart blocks 2, 8 and 9; the channel stops when its own variant
    /// is gone, ChannelingUpdateStats blocks 4 and 5).
    /// </summary>
    private const string Suppression = "OdinGuardianSuppression";

    /// <summary>The quest target's mark on its guardian (CharScriptOdinQuestIndicator; its OnBuffActivate draws odin_point_active.troy per team).</summary>
    private const string QuestIndicator = "OdinQuestIndicator";

    /// <summary>
    /// Batch 662: the guardians' AI, the plain turret script (<see cref="GuardianTurretAi"/>); one script instance per guardian net id, so a
    /// replaced guardian's replacement starts its own. Created on the first step that has the tower hooks.
    /// </summary>
    private GuardianTurretAi? _turretAi;

    /// <summary>
    /// Batch 662 (owner, 3 November 2026: "Normal turret"; replaces batch 501's OdinTowerBehavior tree and its ally gate): each guardian of an
    /// owned point runs Turret.luaobj (<see cref="GuardianTurretAi"/>): every 0.15 s it looks for the nearest summoned unit, else minion, else
    /// any enemy within its 950 acquisition range, keeps it while it is within its Spell1's 1000, and casts that Spell1
    /// (OdinGuardianSpellAttackCast, cooldown 1 s) at it whenever the slot is ready. The shot of that cast is batch 481b's (OdinGuardianShot).
    /// The script does not run for a neutral or a channelled guardian (batch 466's gate below, kept).
    /// Batch 466: the guardian fires only while its team holds the point: a neutral guardian attacks nothing (wiki Capture_point, Basis >
    /// Ownership: "If the point has not been fully captured from neutral, it will not attack any units"; OdinGuardianBuff gives the neutral guardian both teams' sight and no team of its own). It holds its fire while a
    /// champion channels the capture on it: the shipped channel's ChannelingStart adds OdinGuardianSuppression to its Target (OdinCaptureChannel
    /// block 2, 30 s, renewed), whose OnBuffActivate stuns the guardian (BBApplyStun 0.5 s; OdinGuardianSpellAttackCast casts nothing while it
    /// holds Stun) and whose stats hook clears it once no channel stack is left. Reconstruction: the hold for the whole channel rather than the
    /// stun's half second is the wiki's "will stop attacking when being neutralized by the enemy champion" (lead section); the AI that read the
    /// suppression did not ship. The hold reads the whole family: OdinGuardianSuppression lasts while one team channels (batch 467: its stats
    /// hook now runs after the channel's team stack is added) but its own stats hook clears it while both teams channel (BuffOnUpdateStats
    /// block 3), and the team variants last as long as each channel.
    /// </summary>
    private void GuardiansAttack(double now, double dt)
    {
        if (Context.Capture?.Tower is not { } tower) return;
        _turretAi ??= new GuardianTurretAi(Context.World, tower, Context.Note);
        foreach (var guardian in _guardians.Values)
        {
            // Batch 660: a guardian that does not run fires nothing, so it shows no target line; the line's publisher sends only a change.
            if (!guardian.Alive || guardian.Team is not (Team.Order or Team.Chaos) ||
                guardian.Buffs.Any(buff => buff.Name.StartsWith(Suppression, StringComparison.Ordinal)))
            {
                tower.TargetLine?.Invoke(guardian, null);
                continue;
            }
            tower.TargetLine?.Invoke(guardian, _turretAi.Step(guardian, now, dt));
        }
    }

    /// <summary>
    /// Batch 458 (Reconstruction): the opening barriers. Each base has a shipped barrier, the OdinOpeningBarrier encounter
    /// (LEVELS/Map8/Encounters/OdinOpeningBarrier.json) at its named location (Locations.dat OrderBarrier = Info_Spawn_Barrier1,
    /// ChaosBarrier = Info_Spawn_Barrier2); its character script raises the forcefield (OdinOpeningBarrierParticle: green to its own team,
    /// red to the other). No shipped tree or script spawns it or opens it, so it follows the opening ceremony's hold
    /// (<c>SetUnitCircularMovementRestriction</c>): a team's barrier stands from the step the ceremony first holds one of its champions and
    /// opens (dies) on the step the ceremony lets them go. The barrier's own 80-second buff (CharScriptOdinOpeningBarrier) would end it
    /// later than the ceremony's release (80 s on this server, NOT RETAIL; the shipped 45, see OpeningCeremonyTiming); the ceremony's time
    /// wins, so the forcefield and the hold end together.
    /// </summary>
    private static readonly (Team Team, string Location)[] OpeningBarriers = [(Team.Order, "OrderBarrier"), (Team.Chaos, "ChaosBarrier")];

    /// <summary>
    /// Batch 474e (Reconstruction): the ceremony's hold (OdinScriptBehaviorTrees.xml:4711-4727, <c>SetUnitCircularMovementRestriction</c> with
    /// <c>SpawnBarrierRadius</c> 1200 about the team's start point) is set on the host's first tick only (<c>__IsFirstRun</c>, :4631), over the
    /// champions in the world then. A champion who is not in the world on that tick, or joins later, would never be held. Until the release
    /// (<see cref="OpeningCeremonyTiming.ReleaseSeconds"/>) every champion without a hold gets the ceremony's hold, for both teams.
    /// </summary>
    private void HoldLateChampions(double now)
    {
        if (now >= OpeningCeremonyTiming.ReleaseSeconds || Context.World is null) return;
        foreach (var hero in Context.World.Heroes())
        {
            if (hero.MovementRestriction is not null || hero.Team is not (Team.Order or Team.Chaos)) continue;
            var start = Context.NamedLocation(hero.Team == Team.Chaos ? "ChaosStartPoint" : "OrderStartPoint");
            if (start is null) continue;
            hero.MovementRestriction = (new Position2(start.Value.X, start.Value.Z), 1200.0);
        }
    }

    /// <summary>The standing barrier squad of each team; 0 once it has opened. A team with no entry has not been held yet.</summary>
    private readonly Dictionary<Team, int> _barriers = new();

    /// <summary>Batch 477b (Reconstruction): the stairs' closing stages, each sent once at its start.</summary>
    private readonly DominionProps.StairAnimator _stairs = new();

    /// <summary>Batch 507: the stairs' lasers into the nexus crystal, each stage sent once at its latch.</summary>
    private readonly DominionProps.CrystalBeams _beams = new();

    /// <summary>Batch 477d (Reconstruction): the stairs' Raise and Raised_Idle after Close4 (the Odin_SoG prop carries the nexus crystal).</summary>
    private readonly DominionProps.StairRaiser _stairRaiser = new();

    /// <summary>Reconstruction (batches 477d, 483 and 491): the stairs' and the crystal prop's shatter, once a team's nexus falls.</summary>
    private readonly DominionProps.NexusDeath _nexusDeath = new();

    /// <summary>NOT RETAIL: whether the first start message has gone out (it goes out at <see cref="OpeningCeremonyTiming.FirstAnnouncementSeconds"/>).</summary>
    private bool _firstAnnouncementSent;

    private void UpdateOpeningBarriers()
    {
        if (Context.Encounters is not { } encounters) return;
        foreach (var (team, location) in OpeningBarriers)
        {
            var held = Context.World.Heroes().Any(hero => hero.Team == team && hero.MovementRestriction is not null);
            if (held && !_barriers.ContainsKey(team))
            {
                _barriers[team] = 0;
                if (Context.NamedLocation(location) is not { } at) { Context.Note("barrier:" + location, $"the map places no {location}"); continue; }
                if (encounters.CreateEncounter("OdinOpeningBarrier", 1, "") is not { } encounter) { Context.Note("barrier", "no OdinOpeningBarrier encounter"); continue; }
                _barriers[team] = encounters.SpawnSquad(encounter, new Position2(at.X, at.Z), team, "") ?? 0;
            }
            else if (!held && _barriers.GetValueOrDefault(team) is var squad and not 0)
            {
                _barriers[team] = 0;
                foreach (var unit in encounters.Squad(squad)?.Units.Where(unit => unit.Alive) ?? [])
                {
                    if (Context.Capture?.Kill is { } kill) kill(unit, 0u);
                    else Context.Note("barrier-kill", "this context has no kill hook: the barrier does not open");
                }
            }
        }
    }

    /// <summary>The Odin wave's manager (OdinScript.xml's first quest): its init creates the wave encounter and the wave clock.</summary>
    private const string WaveTree = "CapturePointManager";

    /// <summary>Batch 499: the shipped wave spawner, SpawnMinionWave (OdinScriptBehaviorTrees.xml:3784-3906).</summary>
    private const string WaveSpawner = "SpawnMinionWave";

    // Batch 499: each lane's route as its first squad got it (OdinWaves.Route), by nav list and direction; every squad of a lane starts on its marker.
    private readonly Dictionary<(int Nav, bool Reverse), IReadOnlyList<Position2>> _laneRoutes = new();

    /// <summary>
    /// Batch 499, the wave caller. CapturePointManager creates SmallWave (<c>WaveEncounterID</c>, OdinScriptBehaviorTrees.xml:2031), sets
    /// <c>NextWaveSpawnTime</c> to the game time (:2039) and <c>WaveSpawnPeriod</c> 25 (:2045), and no shipped node reads them. The shipped spawner
    /// is the SpawnMinionWave tree, which runs here as shipped: it spawns nothing for a neutral point or toward a point its team already holds
    /// (:3824-3841), sets CapturePointMutator's index to the other team's point count (the "catchup" selector, :3851-3878), spawns the squad at
    /// its starting position under its squad name and pushes it down its lane (:3880-3896).
    /// <para>
    /// Reconstruction: no shipped tree calls SpawnMinionWave (no Procedure node names it; SpawnManagerTest's SpawnWave calls are a test tree's,
    /// and no BTInstance starts it). Each time the game time reaches <c>NextWaveSpawnTime</c> the host adds <c>WaveSpawnPeriod</c> and calls it once for each of
    /// the ten lanes (<see cref="OdinWaves.Lanes"/>): the lane's point and marker give <c>ControllingTeam</c> and <c>StartingPosition</c>, the point
    /// it leads to gives <c>OpposingPointOwner</c>, the squad name is the point's <c>CapPointName</c> (what EvaluateCapturePoint's friendly-minion
    /// branch tells apart, :723-745), and the counts are the points each team holds. So a team's waves leave each point it holds toward each
    /// neighbour it does not hold, every 25 s; no wave spawns before a point is taken.
    /// </para>
    /// </summary>
    private void SpawnWaves(double now)
    {
        var manager = _instances.FirstOrDefault(instance => instance.TreeName == WaveTree);
        if (manager is null || Context.Encounters is not { } encounters) return;
        foreach (var unit in encounters.SpawnDue(now)) BirthWaveUnit(unit);
        var encounterValue = manager.Variable("WaveEncounterID");
        var dueValue = manager.Variable("NextWaveSpawnTime");
        if (encounterValue is null || dueValue is null || _captures.Count != CapturePointNames.Length) return;
        var due = Convert.ToDouble(dueValue, CultureInfo.InvariantCulture);
        if (now < due) return;
        manager.SetVariable("NextWaveSpawnTime", due + Convert.ToDouble(manager.Variable("WaveSpawnPeriod") ?? 25.0, CultureInfo.InvariantCulture));
        var orderPoints = (double)ControlCount(Team.Order);
        var chaosPoints = (double)ControlCount(Team.Chaos);
        foreach (var lane in OdinWaves.Lanes)
        {
            if (Context.NamedLocation(lane.Marker) is not { } at)
            {
                Context.Note("wave-marker:" + lane.Marker, $"the map places no {lane.Marker}");
                continue;
            }
            var call = new BehaviorTreeInstance(Library, WaveSpawner, Context);
            call.SetVariable("StartingPosition", at);
            call.SetVariable("ControllingTeam", WavePointOwner(lane.Point));
            call.SetVariable("OpposingPointOwner", WavePointOwner(lane.Destination));
            call.SetVariable("EncounterID", encounterValue);
            call.SetVariable("LaneID", (double)lane.Nav);
            call.SetVariable("ReverseMinionPath", lane.Reverse);
            call.SetVariable("SquadName", CapturePointNames[lane.Point]);
            call.SetVariable("NumOrderPoints", orderPoints);
            call.SetVariable("NumChaosPoints", chaosPoints);
            call.Tick(now, 0.0);
            if (call.Variable("SquadId") is not { } squad ||
                encounters.Squad(Convert.ToInt32(squad, CultureInfo.InvariantCulture)) is not { } spawned) continue;
            foreach (var unit in spawned.Units) BirthWaveUnit(unit);
        }
    }

    private Team WavePointOwner(int point) => _captures[point].Variable("CurrentPointOwner") is Team owner ? owner : Team.Neutral;

    /// <summary>
    /// Batch 499: a wave unit takes the lane its squad pushes (SquadPushLane): the lane's route and the script of its direction
    /// (<see cref="OdinWaves"/>). SquadPushLane's <c>PatrolMode</c> is read into the squad's lane (BehaviorLeaves.Encounters) and has no
    /// consumer: SpawnWave, the tree the spawners call (OdinScriptBehaviorTrees.xml:3778), passes an empty reference (false); only SpawnMinionWave
    /// (:3894, true) sets it, and no tree calls that one (batch 584). Its server reader did not ship.
    /// Batch 584: no minion portal is spawned, as shipped: the OdinMinionSpawnPortal and OdinMinionGraveyardPortal encounters are named
    /// by no tree, Lua or BTInstance, and the map 8 LevelScript preloads every Odin unit it spawns but not these (nor the OdinMinionPortal
    /// buff). Their scripts make them NoRender, effect-less cubes (the spawn portal untargetable and dead after its 5 s buff).
    /// </summary>
    private void BirthWaveUnit(Entity unit)
    {
        // DIAGNOSTICS: a host without a minion AI (a check's harness) leaves the squad standing on its marker with no route and no script.
        if (Navigation is null || Minions is null || Context.Encounters?.LaneOf(unit) is not { } lane) return;
        if (!_laneRoutes.TryGetValue((lane.Lane, lane.Reverse), out var route))
            _laneRoutes[(lane.Lane, lane.Reverse)] = route = OdinWaves.Route(Context.NamedLocation, lane.Lane, lane.Reverse, unit.Position, Navigation);
        if (route.Count < 2)
        {
            Context.Note("wave-lane:" + lane.Lane, $"the scene has no __NAV_{lane.Lane} list to walk");
            return;
        }
        OdinWaves.Birth(unit, lane.Reverse ? OdinWaves.BackwardScript : OdinWaves.ForwardScript, route,
            new NavigationBounds(Navigation.MinX, Navigation.MaxX, Navigation.MinZ, Navigation.MaxZ), Minions);
    }

    /// <summary>
    /// Batch 461 (Reconstruction): the point's guardian takes the point's new owner as its team. The shipped OdinCaptureChannel ends a capture
    /// as a success only when its Target, the guardian, is on the champion's team (ChannelingUpdateStats: BBGetTeamID of Target and Owner,
    /// CO_EQUAL, BBStopChanneling ChannelingStopCondition_Success), and OdinGuardianBuff draws the guardian by its own team (TEAM_NEUTRAL,
    /// TEAM_ORDER, TEAM_CHAOS branches of OnBuffActivate), so retail's guardian changes team with its point. The shipped trees never touch the
    /// guardian on a capture (their SpawnPointGuard creates and deletes an encounter and returns no squad, :4945), so the change is the engine's.
    /// The guardian is the one standing within the point's radius, the rule the count uses (<see cref="BehaviorLeaves.ChannelsCapture"/>).
    /// Batch 475 (second follow-up, Reconstruction): the team is not changed on the unit (no team-change packet is proven). The neutral guardian
    /// is replaced by one of the new owner's team: AiEntityDataFile.dat:18-21 names a team guardian per owner (OdinOrderGuardian and
    /// OdinChaosGuardian), which the 126 client does not hold, so the team guardian is the neutral character on the owner's team. The old one
    /// leaves the world and every client silently (LeaveVisibility), and no kill or death packet is sent; the new one spawns at the point
    /// through the encounter path (its character script runs again, so OdinGuardianBuff picks its team branch). Its records go out on the
    /// next step, after the spawn's delivery flush (a record for a unit a client does not hold is dropped by the client).
    /// </summary>
    private void GuardianTakesOwner(int position, BehaviorTreeInstance capture, Team owner)
    {
        if (capture.Variable("CapturePosition") is not Position3 at) return;
        // Batch 480b (Reconstruction): the point changes owner through its capture channel, which still runs here (it ends as a success on the
        // guardian after this change, ChannelingUpdateStats), so the channel aimed at the guardian that holds the point on the new owner's side
        // names the capturer. A point taken without a hero's channel leaves no capturer (0).
        _lastCapturerNetIds[owner] = _guardians.TryGetValue(position, out var holder) &&
            Context.Capture?.CapturerOf?.Invoke(holder, owner) is { } capturer
                ? capturer.NetId : 0u;
        var previous = 0u;
        Entity? replaced = null;
        var carried = new List<(string Name, uint CasterNetId, double ExpiresAt, int Stacks)>();
        if (_guardians.Remove(position, out var old))
        {
            previous = old.NetId;
            replaced = old;
            // Batch 479h (Reconstruction): retail keeps this guardian and changes its team in place, so its suppression and team stacks stay on it and
            // an enemy's channel goes on (OdinCaptureChannel ChannelingUpdateStats block 5.1 tests OdinGuardianSuppressionChaos on the guardian). The
            // replacement is a new unit, so these buffs are carried to it below, with their caster, expiry and stack count.
            // Batch 493 (Reconstruction): only the stacks of the channels that go on, the other teams' (the owner's end as a success on the old
            // guardian, EndGuardian below, and their ChannelingSuccessStop takes their stacks), and the suppression after them and only with them:
            // its activation creates odin_suppression.troy, and its stats hook clears it at once when no stack is there yet (BuffOnUpdateStats
            // block 4, both counts 0). G1 showed that blip on every replacement (created and killed in the same step). The capturer's own stack,
            // carried before, was removed by nothing and stayed for the rest of its 30 s, so an enemy channel started in that time found both teams'
            // stacks and its suppression cleared itself (block 3: no drain).
            var stacks = old.Buffs
                .Where(buff => buff.Name == "OdinGuardianSuppressionOrder" && owner != Team.Order ||
                               buff.Name == "OdinGuardianSuppressionChaos" && owner != Team.Chaos)
                .Select(buff => (buff.Name, buff.CasterNetId, buff.ExpiresAt, buff.Stacks)).ToList();
            carried.AddRange(stacks);
            // Batch 550: with them the PrilisasBlessing copies of those channels (OdinCaptureChannel ChannelingStart puts one on the guardian for a
            // Priscilla's Blessing holder; BehaviorLeaves.CaptureRateScale reads it), so a neutralise that goes on to a capture keeps its rate.
            if (stacks.Count > 0)
                carried.AddRange(old.Buffs.Where(buff => buff.Name == "OdinGuardianSuppression" ||
                        buff.Name == "PrilisasBlessing" && Context.World.Get(buff.CasterNetId) is { } holder && holder.Team != owner)
                    .Select(buff => (buff.Name, buff.CasterNetId, buff.ExpiresAt, buff.Stacks)));
            // Batch 570 (Reconstruction): the quest target's mark goes on too, whoever holds the point. CharScriptOdinQuestIndicator gives the
            // guardian within 300 OdinQuestIndicator only in its CharOnActivate (25000 s), and a team neutralises its target before it captures
            // it, so the mark went out at the neutralisation for the rest of the quest. The marker's death still clears it from the replacement
            // (OdinQuestParticleRemover's BuffOnDeath: the units within 300 holding OdinGuardianBuff).
            carried.AddRange(old.Buffs.Where(buff => buff.Name == QuestIndicator).Select(buff => (buff.Name, buff.CasterNetId, buff.ExpiresAt, buff.Stacks)));
            // The channel that captured the point ends when its Target is on the channeller's team (ChannelingUpdateStats, CO_EQUAL), so the
            // old guardian takes the owner's team before it leaves; EndGuardian ends that channel on it as a success.
            old.Team = owner;
            // Batch 493 (Reconstruction): it ends before it leaves: the owner's channels on it end as a success, then its buffs end through the lifecycle
            // (BbSummonerModule.EndReplacedGuardian), so their OnBuffDeactivate kills their particles and capture sounds and removes OdinGuardianBuff's
            // vision bubbles (blocks 6 and 7 give both teams' sight; its deactivation removes them). The loop over Payload.VisionBubbles that stood here
            // removed nothing: a script's bubbles are the world's (BBAddUnitPerceptionBubble), not the payload's.
            Context.Capture?.EndGuardian?.Invoke(old, owner);
            Context.World.Remove(old.NetId);
            // The silent removal: the same LeaveVisibility the replication sends when a unit goes out of sight. Not a death (0xA6, NpcDie).
            // Batch 493: through the replication's own leave (Depart); the 0x54 queued through Send here before was dropped as refresh-rebuilt.
            Context.Capture?.Depart?.Invoke(old);
        }
        // Batch 536 (Reconstruction): the replacement's squad is not handed to the tree as TurretGuardId. The shipped Neutralization runs
        // KillSquad TurretGuardId (OdinScriptBehaviorTrees.xml:1470) before this change, so with the owned guardian's squad there it killed that
        // guardian: an 0xA6 death to every client (owner game 359f35d6, 31 October: guardians 0x40030008 at 94.42 and 0x40030015 at 204.84),
        // and the death cleared its buffs, so no team stack was carried and the enemy channel that neutralised the point cancelled itself on the
        // replacement (OdinCaptureChannel ChannelingUpdateStats block 5). Retail's guardian keeps its unit through a neutralisation, and the
        // shipped SpawnPointGuard (:4945) writes no SquadId, so the shipped flow names no live squad there. This method removes the old guardian
        // silently (batch 475). The start's TurretGuardId (StartCapturePoints) is the neutral guardian's, which no Neutralization reaches.
        SpawnGuardian(position, at, owner);
        capture.SetVariable("TurretGuardId", 0.0);
        // Batch 512 (Reconstruction): the replacement starts at the tree's progress under its new owner, before its creation block goes out, so
        // the meter goes on from where it was (a capture flips at 99, not 100, so a full spawn pool would step back at the next SetValue).
        if (_guardians.TryGetValue(position, out var fresh) && capture.Variable("CaptureProgress") is double progress)
            fresh.Mana = BehaviorLeaves.CaptureMeterPool(fresh, progress, owner);
        // Batch 479h (Reconstruction): the carried team stacks are on the replacement before the channels are retargeted, so an enemy channel that
        // goes on finds its stack at its next update.
        if (carried.Count > 0 && _guardians.TryGetValue(position, out var carrier)) Context.Capture?.CarryBuffs?.Invoke(carrier, carried);
        // Batch 475b (review): an enemy channel aimed at the removed guardian goes on with the replacement (the one that captured has ended as a
        // success on the old unit, EndGuardian). The channels keep their elapsed time and their hook variables.
        if (replaced is not null && _guardians.TryGetValue(position, out var replacement))
            Context.Capture?.RetargetChannels?.Invoke(replaced, replacement, owner);
        // Batch 475 (second follow-up, root cause): the graveyard's 0xDE create names the point's guardian, and the tree sends it once. The new
        // guardian gets its graveyard's create (and its team state) on the next step, after its creation flush.
        if (previous != 0 && Context.SentCapture.ContainsKey((DominionPackets.RespawnPointOpcode, previous))) _respawnDue.Add(position);
    }

    // Batch 475 (second follow-up): the replacement guardians' graveyard records, sent on the step after the spawn (the spawn's creation flush
    // runs between the steps). The 0xDE create names the guardian (BehaviorLeaves.Relics CreateRespawnPoint), then the team state if one is set.
    private readonly HashSet<int> _respawnDue = new();

    // Batch 510: the guardian's pool (the capture ring and the channel bar, GetPARPercent through the point record, batches 493e and 494) has one
    // writer on the wire. Batch 493a's ease here was a second one: it started from its own last value, so every 0.25 s step went out raw, then the
    // ease pulled it back by most of a step (owner game 1552b1c4, guardian 0x40030000 at 175.076 s: 25727 eased, 26558 raw, 25838 eased).
    // Batch 512 (owner: "one timer"): that writer is the point tree's CapturePoint_SetValue (BehaviorLeaves.CapturePointSetValue), no longer
    // the scripts' BBIncPAR; the creation block carries the pool too (ProductionMatchComposition.ReplicatesPar).

    private void ServeRespawns()
    {
        foreach (var position in _respawnDue.ToArray())
        {
            if (!_guardians.TryGetValue(position, out var guardian)) continue;
            var graveyard = position - 2;
            var create = DominionPackets.CreateRespawnPoint((byte)graveyard, guardian.NetId);
            Context.SentCapture[(DominionPackets.RespawnPointOpcode, guardian.NetId)] = create;
            Context.Capture?.Send?.Invoke(create);
            if (Context.RespawnTeams.TryGetValue(graveyard, out var mask))
            {
                var state = DominionPackets.SetRespawnPointState(guardian.NetId, mask);
                Context.SentCapture[(DominionPackets.RespawnPointOpcode, guardian.NetId)] = state;
                Context.Capture?.Send?.Invoke(state);
            }
            _respawnDue.Remove(position);
        }
    }

    /// <summary>
    /// The capture radius of a point: CapturePointManager's own setting (SetVarFloat CaptureRadius 600, OdinScriptBehaviorTrees.xml:2051),
    /// which overrides EvaluateCapturePoint's parameter default of 500 (:20). The capture order's range is the same radius.
    /// </summary>
    public const double CaptureRadius = 600.0;

    /// <summary>The capture points' named locations, in the order of their CapturePointID (1 to 5).</summary>
    private static readonly string[] CapturePointNames = ["CapturePointA", "CapturePointB", "CapturePointC", "CapturePointD", "CapturePointE"];

    /// <summary>The prototype capture tree every point runs (OdinScriptBehaviorTrees.xml:3, <c>EvaluateCapturePoint</c>).</summary>
    private const string CapturePointTree = "EvaluateCapturePoint";

    /// <summary>
    /// Batch 448: the Odin team turret characters (the only team turret characters shipped; the tree names none). Each point gets one per team.
    /// </summary>
    private static string TurretSkin(Team team) => team == Team.Order ? "OdinOrderTurretShrine" : "OdinChaosTurretShrine";

    /// <summary>
    /// Batch 446, the capture caller (Reconstruction). No shipped file starts EvaluateCapturePoint (batch 446 part 1): the point tree
    /// takes the point's own inputs, so one instance runs per named point, CapturePointID 1 to 5 (Locations.dat names CapturePointA to E).
    /// On the first step each point spawns its guard squad (the PointGuard encounter, see EncounterService) and its two point turrets
    /// (batch 448: one per team, hidden at the start, standing on the point), then its instance starts with the point's inputs: its position,
    /// its id and name, the manager's capture radius (600), an unowned point with no progress, and the guard squad as TurretGuardId. The shipped
    /// trees' own TurretGuardId is never written (SpawnPointGuard names EncounterId, not SquadId), so the caller supplies it.
    /// </summary>
    private void StartCapturePoints()
    {
        _capturesStarted = true;
        for (var index = 0; index < CapturePointNames.Length; index++)
        {
            var name = CapturePointNames[index];
            if (Context.NamedLocation(name) is not { } at)
            {
                Context.Note("capture-point:" + name, $"the map places no location named {name}");
                continue;
            }
            SpawnPointTurrets(index + 2, at);
            var guard = SpawnPointGuard(index + 2, at);
            if (guard is null) Context.Note("capture-guard:" + name, $"no guard squad spawned at {name}");
            var instance = new BehaviorTreeInstance(Library, CapturePointTree, Context);
            instance.SetVariable("CapturePosition", at);
            instance.SetVariable("CapturePointID", (double)(index + 1));
            instance.SetVariable("CapPointName", name);
            instance.SetVariable("CaptureRadius", CaptureRadius);
            instance.SetVariable("CurrentPointOwner", Team.Neutral);
            instance.SetVariable("CaptureProgress", 0.0);
            // Batch 448: the tree's own OrderTurret and ChaosTurret inputs are the point's turrets (EvaluateCapturePoint's parameter
            // docs: "the order turret at the prototype capture point"); GetUnitsInTargetArea needs them as its caster.
            instance.SetVariable("OrderTurret", _turrets.GetValueOrDefault((Team.Order, index + 2)));
            instance.SetVariable("ChaosTurret", _turrets.GetValueOrDefault((Team.Chaos, index + 2)));
            instance.SetVariable("TurretGuardId", (double)(guard ?? 0));
            _captures.Add(instance);
        }
    }

    /// <summary>
    /// Batch 453 (Reconstruction): the relics and speed shrines are placed on map 8, each as its shipped encounter definition
    /// (LEVELS/Map8/Encounters) at the shipped location (Locations.dat). No shipped tree, level script or CreateLevelProps places them
    /// (batch 453 part 1), so the locations are the reconstruction. Health relics: OdinShieldRelic at RelicA to C and LesserRelic1 to 7;
    /// centre relics: OdinCenterRelic_Right at LargeRelic1 and _Left at LargeRelic2 (batch 526, below); speed shrines: SpeedShrine at
    /// SpeedShrine01 and 02. Batch 457:
    /// SpeedShrine03 (11168, 11450) and 05 (6702, 865) lie on blocked cells of map 8's grid (flag 0x02), 1671 and 450 units from the nearest
    /// walkable cell, beyond the shrine aura's 365 (OdinSpeedShrineAura), so no champion could touch them; the wiki counts three shrines on
    /// Dominion (The_Crystal_Scar, Speed Shrines), not four. SpeedShrine04 has no scene object. Batch 506 settles the set: 01, 02 and the
    /// bottom one of the owner's triangle; batch 511 stands the bottom one on its pad, the scene's SigilB (see the table).
    /// Batch 469 (Reconstruction, the wiki's Health Relic (Crystal Scar) and Greater Relic pages; no shipped file holds a time): a relic
    /// first spawns at FirstSpawnAt and comes back RespawnAfter seconds after a pickup. Health relics first spawn at 2:00 and respawn 33
    /// seconds after a pickup; greater relics at 3:00 with a 3-minute respawn. Speed shrines stand from the start and never respawn (their
    /// character makes them invulnerable, CharScriptOdinSpeedShrine).
    /// Batch 476e (the owner's log: the crystals spawned at session 195.75 while the 0xC9 HUD clock (session 16.71) read 2:59; the owner's video
    /// showed them at HUD 3:01): the HUD clock lags our game clock by 0.96 s, so the first spawn is game 181 s + 0.96 (Reconstruction, the
    /// owner's observed timer; the greater relic is not timed in the shipped files). The respawn after a pickup is unchanged.
    /// Batch 516 (Reconstruction; the owner, 31 October 2026: each team can capture only the crystal made for it, its own green and the
    /// enemy's red on the minimap): a centre crystal is on the team that may NOT use it. Every shipped rule then gives the owner's result:
    /// <list type="bullet">
    /// <item>Use: OdinCenterRelic ships <c>Useable*EnemyCanUse</c> only, and <c>IsAllowedToUse</c> (the server's
    /// <c>HeroUseSpell</c>) lets only the other team use a unit with it. So Order uses the Chaos-team crystal and the server refuses Chaos.</item>
    /// <item>Minimap (0xE1, the relative-teams step): a unit on the viewer's own team draws <c>OrderTexture</c>, the other team
    /// <c>ChaosTexture</c>; minimap.ini <c>[CenterRelicLeft]</c>/<c>[CenterRelicRight]</c> give centerShrine*Enemy.dds (red, its commented
    /// OrderColor 255 64 64) and *Friendly.dds (green, ChaosColor 100 255 100; batch 526 decoded the DXT5 pixels: both premises hold).
    /// So each unit's icon is the right colour, but batch 516 put it on the wrong crystal: see batch 526 below.</item>
    /// <item>Particles: OdinBombBuff's <c>BBIf TeamID == TEAM_ORDER</c> gives the red to TEAM_ORDER and the green to the other team, its
    /// <c>BBElse</c> the green to TEAM_ORDER (<c>SpecificTeamOnly</c> and <c>FOWTeam</c> TEAM_ORDER in both). So each viewer sees the
    /// crystal it may use green, as batch 476e's side rule did by hand.</item>
    /// <item>Tooltip (batch 503b): the hover tooltip draws for a minion only on team 300 (<c>[Minion]
    /// NeutralTooltip</c>, "aggressive Monster"); OdinCenterRelic has no tooltip of its own, so a team crystal shows none.</item>
    /// </list>
    /// LargeRelic1 (x 6801, nearer the Order base; batch 476e) is Order's crystal, so it is on Chaos; LargeRelic2 the reverse.
    /// Batch 526 (Reconstruction; the owner's game on 2effd08: each shard's minimap icon showed the other crystal's colour): the shard
    /// icons are 64x64 with the crystal drawn off-centre, centerShrineLeft*.dds at texel x 26.6 (5.4 left of centre) and
    /// centerShrineRight*.dds at 36.6 (4.6 right). With minScale = maxScale = 1 the icon is a fixed 64 screen pixels
    /// centred on the unit, so the two arts sit 10 px apart while the crystals are only 274 world units
    /// apart (about 5 pixels on a 250-pixel minimap of the ~14000-unit map). The art offset is what keeps them apart: [CenterRelicLeft] (centerShrineRight*, drawn right) must
    /// be the east crystal and [CenterRelicRight] the west one. Batch 453 had OdinCenterRelic_Left at LargeRelic1 (west), so the icons
    /// crossed and each colour showed over the other crystal. The encounters now swap places; each crystal's team, use and particles stay.
    /// </summary>
    internal const double HudClockLagSeconds = 0.96;
    private const double CenterRelicFirstSpawnAt = 181.0 + HudClockLagSeconds;
    private static readonly (string Definition, string[] Locations, double FirstSpawnAt, double? RespawnAfter, Team Team)[] RelicPlacements =
    [
        // LesserRelic8 has a scene object (Info_LesserRelic8.sco) but Locations.dat names none for it, so it places nothing (open).
        ("OdinShieldRelic", ["RelicA", "RelicB", "RelicC", "LesserRelic1", "LesserRelic2", "LesserRelic3", "LesserRelic4",
            "LesserRelic5", "LesserRelic6", "LesserRelic7"], 120.0, 33.0, Team.Neutral),
        // Batch 526: _Right (icon art drawn left) on the west crystal, _Left (art drawn right) on the east one; see the summary.
        ("OdinCenterRelic_Right", ["LargeRelic1"], CenterRelicFirstSpawnAt, 180.0, Team.Chaos),
        ("OdinCenterRelic_Left", ["LargeRelic2"], CenterRelicFirstSpawnAt, 180.0, Team.Order),
        // Speed shrines (batch 506, the owner's triangle). Batch 511 (Reconstruction): the bottom shrine stands on the world art's third shrine
        // pad, whose scene object is Info_SigilB (6962.69, 4089.48), not on Info_SpeedShrine5 (6702, 865, in the south wall). Each shrine
        // pad is a pair of small grass patch decals (sml_grass_patch_decalVersion3_no_shadow) with vines (lambert546, tt_vines) in
        // LEVELS/Map8/Scene/room.nvr: the vine pairs centre on (4986, 7786) at SpeedShrine01, (8889, 7780) at 02 and (6947, 4093) at SigilB.
        // None of the three scene objects has an ObjectCFG Move or Rot (Scene/cfg/ObjectCFG.cfg), so each stands at its CentralPoint.
        ("SpeedShrine", ["SpeedShrine01", "SpeedShrine02", "SigilB"], 0.0, null, Team.Neutral),
    ];

    /// <summary>
    /// Batch 469: one place a relic or shrine stands. The encounter is created once; <see cref="Unit"/> is the standing unit (null while it
    /// is gone) and <see cref="SpawnAt"/> the time of its next spawn (the first spawn, or the respawn after a pickup, null when none is due).
    /// </summary>
    private sealed class RelicSlot(string location, int encounterId, Position2 at, double? respawnAfter, double spawnAt, Team team)
    {
        public string Location { get; } = location;
        public int EncounterId { get; } = encounterId;
        public Position2 At { get; } = at;
        public Team Team { get; } = team;
        public double? RespawnAfter { get; } = respawnAfter;
        public Entity? Unit { get; set; }
        public double? SpawnAt { get; set; } = spawnAt;
    }

    private readonly List<RelicSlot> _relics = new();

    private void StartRelicsAndShrines()
    {
        _relicsStarted = true;
        if (Context.Encounters is not { } encounters)
        {
            Context.Note("relics", "no encounter service: the relics and shrines are not spawned");
            return;
        }
        foreach (var (definition, locations, firstSpawnAt, respawnAfter, team) in RelicPlacements)
        {
            foreach (var name in locations)
            {
                if (Context.NamedLocation(name) is not { } at)
                {
                    Context.Note("relic-location:" + name, $"the map places no location named {name} (no scene object): nothing spawns there");
                    continue;
                }
                if (encounters.CreateEncounter(definition, 1, "") is not { } encounter)
                {
                    Context.Note("relic:" + definition, $"no encounter definition named {definition}");
                    continue;
                }
                _relics.Add(new RelicSlot(name, encounter, new Position2(at.X, at.Z), respawnAfter, firstSpawnAt, team));
            }
        }
    }

    /// <summary>
    /// Batch 469 (Reconstruction, the wiki): each relic or shrine place spawns its squad through the encounter path (SpawnSquad, neutral)
    /// once <see cref="RelicSlot.SpawnAt"/> is reached. The unit a pickup kills starts the respawn at its death:
    /// <see cref="RelicSlot.RespawnAfter"/> seconds after the death, at the same place with a new unit. A health relic's pickup kills it
    /// through OdinShieldRelicAura's deactivate. No shipped script applies a greater relic's aura (OdinCenterRelicAura), so a greater
    /// relic's pickup is open: until then only a kill on the combat path starts its respawn. A place with no respawn time (a speed shrine)
    /// is not respawned. The death is seen on the tick after it, so the unit's own death time is the clock of the respawn.
    /// </summary>
    private void ServeRelics(double now)
    {
        foreach (var slot in _relics)
        {
            if (slot.Unit is { Alive: true }) continue;
            if (slot.Unit is { } picked)
            {
                slot.Unit = null;
                slot.SpawnAt = slot.RespawnAfter is { } after ? (picked.DeathTime ?? now) + after : null;
            }
            if (slot.SpawnAt is not { } due || now < due) continue;
            slot.SpawnAt = null;
            if (Context.Encounters is not { } encounters) continue;
            if (encounters.SpawnSquad(slot.EncounterId, slot.At, slot.Team, "") is { } squad
                && encounters.Squad(squad)?.Units.FirstOrDefault() is { } unit)
                slot.Unit = unit;
            else
                Context.Note("relic-spawn:" + slot.Location, $"the relic at {slot.Location} did not spawn");
        }
    }

    /// <summary>
    /// Batch 466 (Reconstruction): the trees' OrderTurret and ChaosTurret of a point (GetTurret, lane 0, position 2 to 6; batch 448) are
    /// server-side handles: a unit of the team that stands on the point, hidden (the init's SetUnitRendered false), which the trees read for
    /// its position (GetUnitPosition, OdinScriptBehaviorTrees.xml:1984-2008) and its team (the caster of GetUnitsInTargetArea, :127, :556-620)
    /// and on which they set render, targetability and a sight modifier. No shipped file creates them and no shipped character fits them:
    /// AiEntityDataFile.dat:18-21 names OdinOrderGuardian and OdinChaosGuardian, characters the client does not hold, and the base shrine
    /// characters batch 448 put here fought with the fountain's 1600 range and 462 damage. The point's attacker is its guardian
    /// (<see cref="GuardiansAttack"/>). So a handle is not in the world: no client is sent it, nothing targets it, and it attacks nothing.
    /// </summary>
    private void SpawnPointTurrets(int position, Position3 at)
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
            _turrets[(team, position)] = new Entity(Context.World.Allocate(UnitType.Turret), EntityKind.Turret, UnitType.Turret, team,
                new Position2(at.X, at.Z)) { Name = TurretSkin(team), ChampionName = TurretSkin(team), NoRender = true };
    }

    // Reconstruction: the guard is the PointGuard encounter, spawned neutral at the point (the shipped SpawnPointGuard creates and
    // deletes that encounter in one step and never spawns a squad from it; see EncounterService).
    private int? SpawnPointGuard(int position, Position3 at) => SpawnGuardian(position, at, Team.Neutral);

    // Batch 475 (second follow-up): a point's guardian on a team (the owner's on a capture, neutral at the start). The encounter path runs the
    // character script again, so OdinGuardianBuff picks the team branch. The guardian is kept in _guardians for PointGuardian, GuardiansAttack,
    // the capture count and the taunt.
    private int? SpawnGuardian(int position, Position3 at, Team team)
    {
        if (Context.Encounters is not { } encounters) return null;
        if (encounters.CreateEncounter("PointGuard", 1, "") is not { } encounter) return null;
        var squad = encounters.SpawnSquad(encounter, new Position2(at.X, at.Z), team, "");
        if (squad is { } id && encounters.Squad(id)?.Units.FirstOrDefault() is { } guardian)
        {
            // Batch 475 (Reconstruction): the owner's test showed a health bar on the point's guardian. No shipped Odin script names a bar hide,
            // so the hide is the engine's: HealthBarHidden goes out as ShowHealthBar false with its buffs (BuildBuffs), as a placemarker's does.
            guardian.HealthBarHidden = true;
            // Batch 512: its pool is the point tree's capture meter from now on (the spawn's own OdinGuardianBuff halving of a neutral one has run).
            guardian.ParIsCaptureMeter = true;
            _guardians[position] = guardian;
        }
        return squad;
    }
}

/// <summary>
/// A map's named locations. <c>Locations.dat</c> names a scene mesh for each location; the mesh's central point in
/// <c>Scene/*.SCB</c> is the position. A name with no mesh file has no position, and the caller decides what that means.
/// </summary>
internal sealed class NamedLocations
{
    private readonly string _scene;
    private readonly Dictionary<string, string> _meshes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Position3?> _points = new(StringComparer.Ordinal);

    public NamedLocations(GameDataPaths map)
    {
        _scene = map.Under("LEVELS", map.MapName, "Scene");
        var table = map.Under("LEVELS", map.MapName, "Locations.dat");
        if (!File.Exists(table)) return;
        foreach (var line in File.ReadLines(table))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 2 && !columns[0].StartsWith(';')) _meshes[columns[0]] = columns[1];
        }
    }

    /// <summary>The scene mesh <c>Locations.dat</c> names for <paramref name="name"/>. Batch 499: a name it does not list is a scene mesh of its own
    /// name (the wave lanes' __NAV_&lt;lane&gt;_&lt;index&gt;, OdinWaves).</summary>
    public string Mesh(string name) => _meshes.GetValueOrDefault(name) ?? name;

    public Position3? Locate(string name)
    {
        if (_points.TryGetValue(name, out var known)) return known;
        Position3? point = null;
        var mesh = Mesh(name);
        var meshPath = CaseInsensitivePaths.Resolve(Path.Combine(_scene, mesh + ".SCB"));
        if (File.Exists(meshPath))
            point = SceneMesh.CentralPoint(meshPath);
        _points[name] = point;
        return point;
    }
}
