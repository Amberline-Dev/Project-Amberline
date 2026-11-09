using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Ai;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Movement;

namespace Amberline.Simulation.Minions;

public readonly record struct PendingMinionSpawn(
    double SpawnAt,
    Team Team,
    MinionType Type,
    int Slot,
    string Lane,
    double ScheduledElapsed,
    MinionStatDefinition Stats,
    string Name);

/// <summary>
/// Per-world wave clock and stagger queue. The scheduler has no static queue,
/// so parallel matches cannot consume one another's spawn entries. The composition of every
/// wave, its interval and its stagger come out of the map's shipped <c>LevelScript.luaobj</c>
/// (<see cref="LevelScriptHost"/>); this type owns the clock and the queue only. The lanes are the map's
/// (<see cref="MapDefinitions.Lanes"/>: Summoner's Rift's three, Map4's two scene lanes since batch 556).
/// </summary>
public sealed class WaveScheduler
{
    private readonly WorldState _world;
    private readonly NavigationGrid _navigation;
    private readonly IMinionPacketSink _packets;
    private readonly LevelScriptHost _levelScript;
    // The MeshSkin of every lane minion this match spawns (GameMaps.HarrowingMinionSkinId on the Harrowing rift, else 0).
    private readonly int _laneMinionSkinId;
    private readonly GameData _data;
    private readonly BuffLifecycle? _buffs;
    private readonly List<PendingMinionSpawn> _pending = new();
    private readonly Dictionary<Team, int> _teamBudget = new();
    private readonly Dictionary<(Team Team, string Lane), IReadOnlyList<Position2>> _routeCache = new();
    private readonly Dictionary<string, (IReadOnlyList<Position2> Route, IReadOnlyList<int> At)> _stitchedRoutes = new();
    private readonly double _firstSpawnSeconds;
    private double _intervalSeconds;
    // No fixture override: each wave's own WaveSpawnRate sets the time to the next one (ScheduleWave).
    private readonly bool _scriptInterval;
    private double? _startClock;
    private double? _nextSpawnTime;
    private readonly bool _laneWaves;
    private double _lastStep;
    private int _waveIndex;

    public WaveScheduler(WorldState world, NavigationGrid navigation, IMinionPacketSink packets,
        LevelScriptHost levelScript, double? firstSpawnSeconds = null, double? intervalSeconds = null,
        GameData? gameData = null, BuffLifecycle? buffs = null, int laneMinionSkinId = 0, bool laneWaves = true)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _packets = packets ?? throw new ArgumentNullException(nameof(packets));
        _levelScript = levelScript ?? throw new ArgumentNullException(nameof(levelScript));
        _data = gameData ?? GameData.Default;
        _buffs = buffs;
        _laneMinionSkinId = laneMinionSkinId;
        _laneWaves = laneWaves;
        // Reconstruction: GetTotalTeamMinionsSpawned is a zero-returning client stub:
        // its server scope is unknown. Answer with live lane minions for the asking team plus that team's
        // pending members, snapshotted once per team per wave so the three lanes of one wave see
        // one population — the policy the hand-written composition applied.
        _levelScript.TotalTeamMinionsSpawned ??= team =>
            _teamBudget.TryGetValue(team, out var snapshot) ? snapshot : LivePlusPending(team);
        // The script's own values unless a source fixture overrides the grid.
        _firstSpawnSeconds = firstSpawnSeconds ?? _levelScript.FirstSpawnSeconds;
        _intervalSeconds = intervalSeconds ?? MinionDefinitions.WaveIntervalSeconds;
        _scriptInterval = intervalSeconds is null;
        // Map3 sets INITIAL_TIME_TO_SPAWN -90000000 and plays no lane waves (batch 563), so only a map with waves needs a first wave time.
        if (!double.IsFinite(_firstSpawnSeconds) || (laneWaves && _firstSpawnSeconds < 0.0))
            throw new ArgumentOutOfRangeException(nameof(firstSpawnSeconds));
        if (!double.IsFinite(_intervalSeconds) || _intervalSeconds <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
    }

    public bool Started => _startClock is not null;
    public double? StartClock => _startClock;
    public int WaveIndex => _waveIndex;
    /// <summary>The shipped script host this scheduler's waves, timers and economy come from.</summary>
    public LevelScriptHost LevelScript => _levelScript;
    public IReadOnlyList<PendingMinionSpawn> Pending => _pending;
    /// <summary>
    /// Test-only control for source fixtures that inject explicit wave 2/3/4
    /// members. Live matches leave automatic scheduling enabled.
    /// </summary>
    internal bool AutomaticSpawningEnabled { get; set; } = true;
    public IReadOnlyList<string> ActiveLanes { get; private set; } = MapDefinitions.Lanes;

    /// <summary>
    /// Batch 674 (Reconstruction, Battle Training): no wave is scheduled while held; the level's own timers still run. The announcer's line
    /// (wiki, Announcer/Tutorial): minions "normally begin spawning after ninety seconds, but for this training they will not spawn until
    /// your champion" leaves the base. <see cref="ReleaseWaves"/> ends the hold.
    /// </summary>
    public bool WavesHeld { get; set; }

    /// <summary>Ends <see cref="WavesHeld"/>: before the first wave, the first wave is due at once; later waves keep their cadence.</summary>
    public void ReleaseWaves(double now)
    {
        if (!WavesHeld) return;
        WavesHeld = false;
        if (_waveIndex == 0 && _nextSpawnTime is { } next && next > now) _nextSpawnTime = now;
    }

    /// <summary>
    /// Binds a freshly created lane minion to its shipped character script (batch 159 part 2), the seam the match
    /// composition points at <c>BbUnitCharacterModule.BindUnit</c>. Null when no script engine is installed: the minion
    /// then keeps the hand-written path and gets none of the buffs its character script asks for. It runs at creation,
    /// right after the create burst, where the hand-written siege and super buffs used to be placed: the buffs the
    /// character script asks for are then the first thing the reveal of a fogged enemy minion publishes.
    /// </summary>
    public Action<Entity, string>? BindUnitCharacter { get; set; }

    public void Start(double startClock, IEnumerable<string>? activeLanes = null)
    {
        if (Started) throw new InvalidOperationException("WaveScheduler.Start may only be called once.");
        if (!double.IsFinite(startClock)) throw new ArgumentOutOfRangeException(nameof(startClock));
        var lanes = (activeLanes ?? MapDefinitions.Lanes).ToArray();
        if (lanes.Length == 0 || lanes.Any(lane => !MapDefinitions.Lanes.Contains(lane, StringComparer.Ordinal)))
            throw new ArgumentException("activeLanes must contain lanes of the match's map.", nameof(activeLanes));
        ActiveLanes = lanes;
        _startClock = startClock;
        _lastStep = startClock;
        _nextSpawnTime = startClock + _firstSpawnSeconds;
        // OnLevelInit sets the spawn table and arms the level timers the wave path depends on.
        _levelScript.OnLevelInit(startClock);
    }

    /// <summary>Builds both teams' routes of every lane of the map (<see cref="MapDefinitions.Lanes"/>) before traffic starts.</summary>
    public void WarmAllRoutes()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var lane in MapDefinitions.Lanes)
            _ = LaneRoute(team, lane);
    }

    /// <summary>Schedules due waves and drains all entries due at <paramref name="now"/>.</summary>
    public IReadOnlyList<Entity> Tick(double now)
    {
        if (!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now));
        if (!Started) return Array.Empty<Entity>();
        // The level's own timers run before the barracks ask the script for a wave, so an upgrade
        // or a cannon-cadence change lands in the wave it should. dt is the whole step: the host
        // arms a timer lazily on its first step (startClock + interval).
        _levelScript.Advance(now, now - _lastStep);
        _lastStep = now;
        if (!AutomaticSpawningEnabled || WavesHeld) return Array.Empty<Entity>();
        // The stock barracks update fires once per update when the time since its last trigger
        // reaches WaveSpawnRate, counts the wave before it calls Lua, and stores the trigger time: a wave missed during a
        // stall is never replayed and the next one is due a whole interval after the trigger. A tick that is merely a few
        // milliseconds late keeps the nominal grid (the wave is stamped with its nominal time); only a stall of a full
        // interval or more re-anchors the cadence to the current time.
        if (now >= _nextSpawnTime!.Value)
        {
            var waveClock = now - _nextSpawnTime.Value < _intervalSeconds ? _nextSpawnTime.Value : now;
            ScheduleWave(waveClock);
            _nextSpawnTime = waveClock + _intervalSeconds;
            _waveIndex++;
        }

        var ready = _pending.Where(spawn => spawn.SpawnAt <= now).ToArray();
        if (ready.Length == 0) return Array.Empty<Entity>();
        _pending.RemoveAll(spawn => spawn.SpawnAt <= now);
        var spawned = new List<Entity>(ready.Length);
        foreach (var request in ready)
            spawned.Add(Spawn(request, now));
        return spawned;
    }

    public IReadOnlyList<Position2> LaneRoute(Team team, string lane)
    {
        ValidateLane(lane);
        if (_routeCache.TryGetValue((team, lane), out var cached)) return cached;
        var spine = MapDefinitions.LanePaths[lane];
        if (!_stitchedRoutes.TryGetValue(lane, out var stitched))
        {
            var builtRoute = new List<Position2> { spine[0] };
            var builtAt = new List<int> { 0 };
            for (var index = 1; index < spine.Count; index++)
            {
                // Batch 556, Reconstruction: a lane node the NPC pathing grid refuses is walked to the nearest pathing cell instead (Map4's
                // __NAV_R015 lies 105 units from Turret_T2_R_01, inside its 140-unit footprint), the correction the Summoner's Rift port made
                // by hand to three of its points (MapDefinitions' lane note). The spawns stay the barracks markers, as Summoner's Rift's L and
                // R spawns are (its Order R spawn lies in the HQ stamp too). No Summoner's Rift node is refused, so its routes do not change.
                var node = index < spine.Count - 1 ? _navigation.ProjectToPathingFloor(spine[index]) : spine[index];
                var leg = _navigation.FindPath(builtRoute[^1], node).ToList();
                if (leg.Count == 0) leg = [builtRoute[^1], node];
                builtRoute.AddRange(leg.Skip(1));
                builtAt.Add(builtRoute.Count - 1);
            }
            stitched = (builtRoute, builtAt);
            _stitchedRoutes[lane] = stitched;
        }
        var route = stitched.Route;
        var at = stitched.At;

        IReadOnlyList<Position2> selected;
        var approach = MapDefinitions.LaneSpawnApproaches[lane];
        if (team == Team.Order)
        {
            var end = at[at.Count - 1 - approach.Tail];
            selected = route.Take(end + 1).ToArray();
        }
        else
        {
            selected = route.Skip(at[approach.Head]).Reverse().ToArray();
        }
        selected = Geometry.ClientWaypoints(selected,
            new NavigationBounds(_navigation.MinX, _navigation.MaxX,
                _navigation.MinZ, _navigation.MaxZ));
        _routeCache[(team, lane)] = selected;
        return selected;
    }

    /// <summary>
    /// Creates one minion through the production wave spawn path for an
    /// offline capacity fixture. Stats come from the script's barracks table as
    /// of this host's own level clock (advance it with
    /// <see cref="LevelScriptHost.AdvanceTo"/> to age the economy), while
    /// <paramref name="now"/> remains the match clock used for lifecycle anchors.
    /// </summary>
    public Entity SpawnFixture(Team team, MinionType type, int slot, string lane,
        double now, double scheduledElapsed)
    {
        if (!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now));
        if (!double.IsFinite(scheduledElapsed) || scheduledElapsed < 0.0)
            throw new ArgumentOutOfRangeException(nameof(scheduledElapsed));
        if (slot < 0) throw new ArgumentOutOfRangeException(nameof(slot));
        ValidateLane(lane);
        return Spawn(new PendingMinionSpawn(now, team, type, slot, lane,
            scheduledElapsed, _levelScript.EconomyOf(team, lane).StatsOf(type, team),
            MinionDefinitions.CharacterName(type, team)), now);
    }

    private void ScheduleWave(double waveClock)
    {
        // NOT RETAIL: The Crystal Scar's waves are Odin minions on Odin lanes (batch 441 section 3.6: their AI is a later phase), so the
        // map plays no lane wave until then. Summoner's Rift's lanes are not this map's lanes.
        if (!_laneWaves) return;
        // The nominal grid, not waveClock - startClock, so a fractional start clock leaves the
        // recorded wave stamps on the same grid the checks group by.
        var scheduledElapsed = Math.Max(_firstSpawnSeconds + _waveIndex * _intervalSeconds, waveClock - _startClock!.Value);
        foreach (var team in new[] { Team.Order, Team.Chaos })
        {
            _teamBudget[team] = LivePlusPending(team);
            try
            {
                foreach (var lane in ActiveLanes)
                {
                    // The shipped LevelScript answers every one of these: the four counts, the
                    // character names, the wave interval and the stagger between its members — and,
                    // through the same barracks table, the economy row each member is born with.
                    // The shared obj_Barracks update (batch 310) reads this table once,
                    // at the wave's tick, into the barracks' own fields (counts, ExpGiven,
                    // GoldGiven, HP and damage bonuses, armor, magic resistance), so
                    // this ask is the wave's only snapshot and a delayed drain cannot split a staggered wave.
                    var wave = _levelScript.GetMinionSpawnInfo(team, lane, _waveIndex);
                    // Batch 556: the barracks waits its table's WaveSpawnRate for the next wave (the update above reads it into the
                    // barracks with the rest of the answer): Summoner's Rift's 30000, Map4's 35000 (LevelScript.txt proto 0.1 pc 0063).
                    if (_scriptInterval && wave.WaveSpawnRateSeconds > 0.0) _intervalSeconds = wave.WaveSpawnRateSeconds;
                    var members = wave.Members;
                    var stagger = wave.SingleMinionSpawnDelaySeconds;
                    for (var slot = 0; slot < members.Count; slot++)
                    {
                        _pending.Add(new PendingMinionSpawn(
                            waveClock + slot * stagger,
                            team, members[slot], slot, lane,
                            scheduledElapsed + slot * stagger,
                            wave.Economy.StatsOf(members[slot], team), wave.NameOf(members[slot])));
                    }
                }
            }
            finally
            {
                _teamBudget.Remove(team);
            }
        }
    }

    private int LivePlusPending(Team team) =>
        MinionDefinitions.CurrentLiveTeamMinionCount(_world, team) +
        _pending.Count(spawn => spawn.Team == team);

    private Entity Spawn(PendingMinionSpawn request, double now)
    {
        var stats = request.Stats;
        var name = request.Name;
        var route = LaneRoute(request.Team, request.Lane);
        if (route.Count == 0) throw new InvalidOperationException("The lane route is empty.");
        var initialFacing = RouteFacing(route, request.Team);
        var entity = new Entity(_world.Allocator.Alloc(UnitType.Minion), EntityKind.Minion,
            UnitType.Minion, request.Team, route[0])
        {
            MinionType = request.Type,
            MinionLane = request.Lane,
            SkinId = _laneMinionSkinId,
            WaveSlot = request.Slot,
            SpawnScheduledAt = request.ScheduledElapsed,
            NodeId = MinionDefinitions.EnemyNodeId,
            Speed = stats.MoveSpeed,
            Radius = stats.Radius,
            CombatRadius = _data.Champion(name).CombatRadius,
            Health = stats.MaxHealth,
            MaxHealth = stats.MaxHealth,
            AttackRange = stats.AttackRange,
            AcquisitionRange = _data.Champion(name).AcquisitionRange,
            AttackDamage = stats.AttackDamage,
            MissileSpeed = stats.MissileSpeed,
            AttackWindup = stats.AttackWindup,
            AttackPeriod = stats.AttackPeriod,
            AttackTimingBaseline = new AttackTimingBaseline(
                stats.AttackSpeedMod, stats.AttackPeriod,
                stats.AttackPeriod > 0.0 ? stats.AttackWindup / stats.AttackPeriod : 0.0),
            BasicAttackAnimationProbabilities = _data.Champion(name).BasicAttackAnimationProbabilities,
            SelectionRadius = _data.Champion(name).SelectionRadius,
            AttackSpeedMod = stats.AttackSpeedMod,
            HealthRegen = stats.HealthRegen,
            Armor = stats.Armor,
            SpellBlock = stats.SpellBlock,
            GoldReward = stats.GoldReward,
            ExperienceReward = stats.ExperienceReward,
            Name = name,
            LaneDone = false,
            MinionState = MinionAiState.Idle,
            Facing = initialFacing,
            Created = request.Team == Team.Order,
            Visible = request.Team == Team.Order,
        };
        // The source does not stash the lane suffix until the first aggro
        // transition. Keeping it empty here prevents return-to-lane logic
        // from treating the entire unconsumed route as a stale rejoin anchor.
        entity.LaneWaypoints = new List<Position2>();
        // The lane route is the minion's first path, installed the way the client installs every path (MovementSystem.InstallPath).
        route = MovementSystem.InstallPath(entity, route,
            new NavigationBounds(_navigation.MinX, _navigation.MaxX, _navigation.MinZ, _navigation.MaxZ));
        entity.LastAnchorTime = now;
        _world.Register(entity);
        // Retail builds a minion's AI object when the unit is created, so minionLib 0.0 OnInit runs
        // here and not at the unit's first AI step (LuaAiService.InstanceFor's Reconstruction note).
        // Otherwise the first callback to reach a minion — a lethal edge, a call for help, a control
        // buff — created the instance and ran OnInit first, and OnInit's SetState(AI_IDLE) reset the
        // state that callback was raised for. The host still creates one lazily for a unit it has
        // never seen, so this is only the ordinary birth of it.
        _buffs?.Minions?.OnInit(entity);

        // A friendly wave is immediately observable. A fogged enemy wave is
        // held until the observer service creates it at first reveal.
        if (request.Team == Team.Order)
        {
            _packets.SendCreate(entity);
            _packets.SendVisibility(entity);
            _packets.SendStats(entity);
            if (_packets.IsReferenceReady(entity))
            {
                _packets.SendWaypoints(entity, route, _world.Clock.SyncId(), "wave-spawn");
                entity.AnchorPath = route.ToList();
            }
        }
        // Batch 159 part 2: the siege and super characters' own scripts are what put their buffs on them —
        // CharScriptBlue_Minion_MechCannon's UpdateSelfBuffStats adds TurretShield when it is missing (0000-0038),
        // CharScriptBlue_Minion_MechMelee's CharOnActivate adds TurretShield and StructureDamageReduction once
        // (0054-0096). The hand-written AddTurretShield / StructureDamageReduction adds are gone; the bind is here,
        // after the create, so the buffs reach the client the way the stand-ins did (a revealed Chaos minion still
        // publishes them at its reveal).
        BindUnitCharacter?.Invoke(entity, name);
        return entity;
    }

    private static Position2 RouteFacing(IReadOnlyList<Position2> route, Team team)
    {
        if (route.Count > 1)
        {
            var delta = route[1] - route[0];
            var length = Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
            if (length > 1e-9)
                return new Position2(delta.X / length, delta.Z / length);
        }
        return team == Team.Order ? new Position2(0.0, 1.0) : new Position2(0.0, -1.0);
    }

    private static void ValidateLane(string lane)
    {
        if (!MapDefinitions.Lanes.Contains(lane, StringComparer.Ordinal))
            throw new ArgumentException($"The match's map has no lane '{lane}'.", nameof(lane));
    }
}
