using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.Ai;

namespace Amberline.Simulation.Neutral;

/// <summary>
/// The neutral camps of the match's map, as that map's own <c>NeutralMinionSpawn.luaobj</c> decides them: which
/// generation is up, what is in it, what health and damage bonus it carries, and when an emptied
/// camp comes back. The engine keeps the part the script has no say in — creating the monster
/// entities on legal terrain, wiring them to the client, and the empty-camp marker — and every
/// schedule number comes out of the script's own camp table and its respawn closures. The hand
/// written spawn/respawn clock it used to keep is gone.
/// It is deliberately per-world: network IDs, camp state, and player-level scaling never cross a
/// match boundary.
/// </summary>
public sealed class JungleScheduler
{
    private const double SpawnClearanceMargin = 4.0;
    private const int SpawnSearchRadiusCells = 12;
    private readonly WorldState _world;
    private readonly GameData _gameData;
    private readonly INeutralPacketSink? _wire;
    private readonly Dictionary<int, NeutralCampState> _camps;
    private readonly NavigationGrid? _navigation;
    private readonly List<(Position2 Position, double Radius)> _occupied = new();
    private readonly Dictionary<uint, NeutralMember> _members = new();
    // The UniqueNames string InitializeNeutralMinionInfo gave each member. NeutralMinionDeath
    // matches its argument against that table, so a death has to be reported under it.
    private readonly Dictionary<uint, string> _uniqueNames = new();
    private readonly LevelScriptHost _level;
    private readonly bool _shared;
    private double? _startedAt;
    private double _initialSpawnAt;
    private bool _initialSpawned;
    private double _lastStep;

    public JungleScheduler(WorldState world, GameData gameData,
        INeutralPacketSink? wire = null,
        IEnumerable<int>? campIds = null, NavigationGrid? navigation = null, LevelScriptHost? levelScript = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        _wire = wire;
        _navigation = navigation;
        // The camps of this data's map: that map's own NeutralMinionSpawn.luaobj builds them (Summoner's Rift twelve, the Harrowing rift thirteen).
        var mapCamps = NeutralDefinitions.CampsFor(_gameData.Paths);
        var selected = campIds is null
            ? mapCamps
            : mapCamps.Where(pair => campIds.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        // A named camp list must name a camp of this map. The map's own list may be empty: The Crystal Scar's level script never chains
        // its NeutralMinionSpawn (batch 443), so that map has no camp and its jungle is empty, not refused.
        if (campIds is not null && selected.Count == 0)
            throw new ArgumentException("At least one neutral camp is required.", nameof(campIds));
        _camps = selected.ToDictionary(pair => pair.Key,
            pair => new NeutralCampState(pair.Value));
        // Map1's own map-script environment. A match keeps one for the whole level, and the composition passes the wave
        // scheduler's host so that the camp chunks run in it (retail keeps one script environment). A standalone scheduler
        // (the checks) owns its host and runs the level init and the timers itself.
        // DIAGNOSTICS (test seam, batch 219 item 3): a standalone scheduler keeps a second environment, because the checks
        // have no wave scheduler. Production never takes this branch: ProductionMatchComposition passes its level host, the
        // only construction in src/, so only the checks reach the default.
        _shared = levelScript is not null;
        _level = levelScript ?? new LevelScriptHost(_world, new AiScriptCatalog(_gameData.Paths));
        _level.SpawnNeutralMember = SpawnMember;
        _level.NeutralTimerArmed = OnCampTimerArmed;
    }

    public double? StartedAt => _startedAt;
    /// <summary>Map1's LevelScript running this match's camps; the checks read its tables from here.</summary>
    public LevelScriptHost LevelScript => _level;
    public IReadOnlyDictionary<int, NeutralCampState> Camps => _camps;
    public IReadOnlyDictionary<uint, NeutralMember> Members => _members;

    /// <summary>
    /// Binds a freshly created monster to its shipped character script (batch 159 part 2), the seam the match
    /// composition points at <c>BbUnitCharacterModule.BindUnit</c>. Null when no script engine is installed: the
    /// monster then keeps the hand-written path and receives none of the buffs its <c>CharOnActivate</c>,
    /// <c>UpdateSelfBuffStats</c> or <c>UpdateSelfBuffActions</c> ask for. It runs before the create burst, so the
    /// buffs the script adds are part of it.
    /// </summary>
    public Action<Entity, string>? BindUnitCharacter { get; set; }

    /// <summary>
    /// The <c>UniqueNames</c> string <c>InitializeNeutralMinionInfo</c> gave each live member
    /// (NeutralMinionSpawn.luaobj proto 0.2), which is the identity its death is reported under.
    /// </summary>
    public IReadOnlyDictionary<uint, string> UniqueNames => _uniqueNames;

    /// <summary>
    /// Chains Map1's <c>NeutralMinionSpawn.luaobj</c> and builds its camp tables
    /// (<c>NeutralMinionInit</c> + <c>InitializeNeutralMinionInfo</c>), then publishes the moment
    /// <c>SpawnInitialNeutralMinions</c> will run plus each camp's own
    /// <c>GroupDelaySpawnTime</c>. The script stores no base delay of its own: 100 s is the
    /// engine's (see <see cref="NeutralDefinitions.InitialSpawnSeconds"/>).
    /// </summary>
    public void Start(double origin = 0.0)
    {
        if (_startedAt is not null)
            throw new InvalidOperationException("jungle scheduler already started");
        if (!double.IsFinite(origin)) throw new ArgumentOutOfRangeException(nameof(origin));
        _startedAt = origin;
        _lastStep = origin;
        if (!_shared)
        {
            // A standalone host runs the level init itself. Its economy and structure timers would move the real structures off
            // the protection ladder (AllowDamageOnBuildings), so they are stopped here; a shared host runs them once, in the wave scheduler's init.
            _level.OnLevelInit(origin);
            _level.StopNamedTimers();
        }
        _level.InitializeNeutralMinionInfo();
        _initialSpawnAt = origin + NeutralDefinitions.InitialSpawnSeconds;
        foreach (var state in _camps.Values)
            state.NextSpawnAt = _initialSpawnAt + state.Definition.InitialDelay;
    }

    public void Tick(double now)
    {
        if (_startedAt is null || !double.IsFinite(now)) return;
        if (!_initialSpawned && now + 1e-9 >= _initialSpawnAt)
        {
            _initialSpawned = true;
            // SpawnInitialNeutralMinions is single-camp (NeutralMinionSpawn proto 0.3) and is run
            // as of the moment it was due, not the moment this step happened to arrive, so a camp
            // whose first generation is delayed still lands on 100 s + GroupDelaySpawnTime.
            foreach (var campId in _camps.Keys.OrderBy(id => id))
                _level.SpawnInitialNeutralMinions(campId, _initialSpawnAt);
        }
        var dt = Math.Max(0.0, now - _lastStep);
        _lastStep = now;
        // A shared host is advanced once per step by the wave scheduler, which owns it.
        if (!_shared) _level.Advance(now, dt);
    }

    /// <summary>
    /// Reports one dead camp member to <c>NeutralMinionDeath</c> and returns whether that emptied
    /// the camp. Every member of a generation is reported, not only the last: the script marks
    /// each one dead in its own <c>AliveTracker</c> and only arms the respawn once the tracker is
    /// all false (proto 0.4). <paramref name="beneficiary"/> is the team the script pays the death
    /// reward to.
    /// </summary>
    public bool OnDeath(Entity monster, double now, Team beneficiary = Team.Neutral)
    {
        ArgumentNullException.ThrowIfNull(monster);
        if (!double.IsFinite(now) || monster.UnitType != UnitType.Monster ||
            monster.Kind != EntityKind.Neutral ||
            !_members.TryGetValue(monster.NetId, out var member)) return false;
        var state = _camps[member.CampId];
        if (!state.Active || !state.MutableMemberNetIds.Contains(monster.NetId)) return false;
        if (_uniqueNames.TryGetValue(monster.NetId, out var uniqueName))
            _level.NeutralMinionDeath(uniqueName, monster, beneficiary, now);
        if (state.MutableMemberNetIds.Any(id => _world.IsAlive(id))) return false;
        state.Active = false;
        state.NextSpawnAt = now + state.Definition.RespawnDelay;
        state.PendingEmpty = true;
        state.BeginEmptyGeneration();
        return true;
    }

    public IReadOnlyList<NeutralMember> MembersForCamp(int campId) =>
        _camps.TryGetValue(campId, out var state)
            ? state.MutableMemberNetIds.Where(_members.ContainsKey).Select(id => _members[id]).ToArray()
            : Array.Empty<NeutralMember>();

    public bool TryGetMember(uint netId, out NeutralMember member) => _members.TryGetValue(netId, out member!);

    /// <summary>
    /// Batch 159 part 2: the camp's player-level growth is <c>Spanws/HPByPlayerLevel.luaobj</c>'s own, so nothing here
    /// scales a monster's hit points. The hand-written <c>ApplyPlayerLevelScaling</c> added 175 / 220 per hero level at
    /// spawn and every ten seconds while the monster was at 99% health, and the shipped buff — added by the character
    /// scripts of AncientGolem, LizardElder and Dragon — added the same amount again, so a golem at level 6 was born
    /// with 3375 where the script's own arithmetic gives 2325 (1400 base + 50 camp bonus + 175 x 5 levels gained since
    /// the level-1 spawn). The script's <c>BuffOnUpdateStats</c> re-takes its sample every 0.25 s and its
    /// <c>BBExecutePeriodically</c> rescans the 9999-unit area every 10 s while the holder is at 99% health or better,
    /// which is the whole of the rule the hand-written ticker stated. What remains here is the remembered level the
    /// old rule published, kept as bookkeeping for the corpus/replay shape only (see <c>Entity.NeutralPlayerLevel</c>).
    /// </summary>
    public NeutralStats? RestampPlayerLevel(NeutralMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (!NeutralDefinitions.PlayerLevelHealth.ContainsKey(member.Name)) return null;
        var entity = member.Entity;
        var level = NearbyHeroLevel(entity.Position);
        if (level <= entity.NeutralPlayerLevel) return null;
        entity.NeutralPlayerLevel = level;
        var updated = member.Stats with { PlayerLevel = level };
        _members[member.NetId] = member with { Stats = updated };
        return updated;
    }

    /// <summary>
    /// The script armed one of its camp closures. That deadline is the script's own answer to
    /// "when does this generation appear", so it replaces whatever the engine predicted.
    /// </summary>
    private void OnCampTimerArmed(int camp, double deadline)
    {
        if (_camps.TryGetValue(camp, out var state))
            state.NextSpawnAt = deadline;
    }

    /// <summary>
    /// One <c>SpawnNeutralMinion</c> request: one member of one generation. Member 1 opens the
    /// generation, releasing the previous one's registry entries and resetting the empty marker.
    /// </summary>
    private bool SpawnMember(LevelNeutralSpawn request)
    {
        if (!_camps.TryGetValue(request.Camp, out var state)) return false;
        if (request.Member == 1) OpenGeneration(state);
        // The script's own camp table at the moment it asked: proto 0.1.0 adds HealthBonus 50.0
        // before every spawn it makes (each respawn, and the first spawn of a camp whose
        // GroupDelaySpawnTime is not zero), and only SpawnInitialNeutralMinions' immediate spawns
        // of a zero-delay camp keep the bonus of 0 the table starts with.
        state.HealthBonus = request.HealthBonus;
        state.DamageBonus = request.DamageBonus;
        var elapsed = request.ScheduledAt - (_startedAt ?? request.ScheduledAt);
        // Retail spawns each member at its scripted point: SpawnNeutralMinion hands member n the script's Positions[n] with
        // no terrain search (NeutralMinionSpawn.luaobj copies the literal Make3DPoint values; the seven points the old search
        // moved are listed in docs/minions/patch126-neutral-spawn-placement.md). The first member is the camp's wire and
        // visibility anchor, so every member and marker uses it.
        // Reconstruction (batch 219, kept by batch 248): a member whose scripted point is a blocked cell keeps the nearest legal cell. The
        // script spawns it on the scripted point, and its route out plans as any unit's does (the engine planner starts on the actor's own
        // cell untested). What retail cannot tell us is its sight from that cell: the client's unit-to-unit LineOfSightTest
        // (the unit's sight test) is an asserting stub, so the server's own rule is unrecoverable,
        // and the port's (NavigationGrid.HasUnitVision, sampled from the viewer's own cell) fails in every direction. Spawned on the
        // scripted points, camp 8 member 2 (wolf) and camp 11 member 1 (golem) see nobody and never fight back
        // (neutral.every-member-fights-back-within-2s, batch 248). Only members on blocked scripted points move; the rest spawn as scripted.
        var provisional = NeutralStatsCalculator.Calculate(_gameData, request.Name, request.HealthBonus, request.DamageBonus,
            spawnTime: elapsed);
        var home = _navigation is not null && !_navigation.IsMovementWalkable(new Position2(request.Position.X, request.Position.Z))
            ? ResolveNearestLegalHome(request.Camp, request.Member - 1, request.Position, provisional.Radius, _occupied)
            : request.Position;
        // Reconstruction (batches 577 and 588): a camp member stands on the terrain, not at its scripted height, on every map. The client
        // keeps the 0x66 position's Y as the unit's height (the packet's height write; the unit's SetPosition,
        // then sets X and Z only) until a move crosses its height gate, so a Y off the ground is drawn in the air.
        // Map4's NeutralMinionSpawn writes spawn heights of +39 to +130 over a jungle floor of -131 (its CampFacePoints carry -131),
        // which put its camps 170 to 261 units up; Map1's and Map2's Worm stands at 24.4 over a pit floor of -72.2 (96.6 up), and their
        // other camps within 18 units of their terrain. Retail's camps stood on the floor; its rule is not shipped. Owner decision,
        // 1 November 2026 (batch 588): "Yes, all maps".
        if (_navigation is not null)
            home = home with { Y = _navigation.HeightAt(new Position2(home.X, home.Z)) };
        if (request.Member == 1) state.CurrentGroupPosition = home;
        var groupPosition = state.CurrentGroupPosition;
        var face = request.Face;
        // Batch 159 part 2: the remembered hero level is bookkeeping here, not a health term. The growth it used to
        // buy is Spells/HPByPlayerLevel.luaobj's own (see RestampPlayerLevel), so the spawn table is asked for the
        // base pool and the script's buff raises it during the bind below; asking the table for the level as well
        // put 175 / 220 per level on the monster twice (a level-6 golem was born with 3375 where both the shipped
        // arithmetic and the recorded camp corpus give 2325).
        var playerLevel = NeutralDefinitions.PlayerLevelHealth.ContainsKey(request.Name)
            ? NearbyHeroLevel(new Position2(home.X, home.Z)) : 0;
        var stats = NeutralStatsCalculator.Calculate(_gameData, request.Name,
            request.HealthBonus, request.DamageBonus, playerLevel: 0, elapsed);
        var name = request.Name;
        var entity = new Entity(_world.Allocate(UnitType.Monster), EntityKind.Neutral,
            UnitType.Monster, Team.Neutral, new Position2(home.X, home.Z))
        {
            Name = name,
            // The network identity becomes observable only after the
            // complete create/stat/buff burst has been emitted.
            Created = false,
            Visible = false,
            Health = stats.Hp,
            MaxHealth = stats.Hp,
            AttackDamage = stats.AttackDamage,
            CritChance = stats.CritChance,
            CritMultiplier = stats.CritMultiplier,
            Armor = stats.Armor,
            SpellBlock = stats.SpellBlock,
            HealthRegen = stats.HpRegen,
            ManaRegen = stats.MpRegen,
            Speed = stats.Speed,
            AttackRange = stats.AttackRange,
            AcquisitionRange = stats.AcquisitionRange,
            Radius = stats.Radius,
            CombatRadius = _gameData.Champion(name).CombatRadius,
            AttackSpeedMod = stats.AttackSpeedMod,
            AttackPeriod = stats.AttackPeriod,
            AttackTimingBaseline = new AttackTimingBaseline(
                stats.AttackSpeedMod, stats.AttackPeriod,
                stats.AttackPeriod > 0.0 ? stats.Windup / stats.AttackPeriod : 0.0),
            BasicAttackAnimationProbabilities = _gameData.Champion(name).BasicAttackAnimationProbabilities,
            SelectionRadius = _gameData.Champion(name).SelectionRadius,
            AttackWindup = stats.Windup,
            MissileSpeed = stats.MissileSpeed,
            GoldReward = stats.GoldReward,
            GlobalGoldReward = stats.GlobalGoldReward,
            ExperienceReward = stats.ExperienceReward,
            GlobalExperienceReward = stats.GlobalExperienceReward,
            Level = request.CampLevel,
            CampId = request.Camp,
            NeutralMember = request.Member,
            NeutralHome = home,
            NeutralGroupPosition = groupPosition,
            NeutralFacePosition = face,
            Facing = VisionPackets.FacingTowards(new Position2(home.X, home.Z), new Position2(face.X, face.Z)),
            NeutralState = "idle",
            NeutralPlayerLevel = playerLevel,
        };
        _world.Register(entity);
        _occupied.Add((new Position2(home.X, home.Z), stats.Radius));
        var member = new NeutralMember(entity, name, request.Camp, request.Member,
            home, groupPosition, face, stats);
        _members[entity.NetId] = member;
        _uniqueNames[entity.NetId] = request.UniqueName;
        state.MutableMemberNetIds.Add(entity.NetId);
        // Batch 159 part 2: the monster's own character script is what puts its inherent buffs on it — AncientGolem's
        // CrestoftheAncientGolem and HPByPlayerLevel, LizardElder's BlessingoftheLizardElder and HPByPlayerLevel,
        // Dragon's ResistantSkinDragon, Wraith's LifestealAttack and its 100% life-steal, the Mech minions' TurretShield
        // and StructureDamageReduction. It runs before the create burst, so those buffs are wired with the unit the way
        // the hand-written stand-ins were; the hand-written AttachInherentBuffs is gone. Batch 174 part 2 binds the Worm
        // here too: CharScriptWorm's UpdateSelfBuffStats adds ResistantSkin and WormRecouperateOn (0000-0038), so
        // AttachWormStandIn is gone as well.
        BindUnitCharacter?.Invoke(entity, name);
        // The scripts' own first sample may have grown the pool (HPByPlayerLevel's IncFlatHPPoolMod on a non-hero moves
        // MaxHealth and Health directly, BuffLifecycle.ApplyNpcMaxHealth), so the member's stat block the create burst
        // and the Worm's attack read is re-stamped from the entity the bind left: the 0xCC that follows the create then
        // carries the script's pool, exactly as the recorded corpora and the live client see it.
        if (entity.MaxHealth != stats.Hp || entity.Health != stats.Hp)
        {
            stats = stats with { Hp = entity.MaxHealth };
            member = member with { Stats = stats };
            _members[entity.NetId] = member;
        }
        if (_wire is not null)
        {
            _wire.CreateNeutral(member);
            foreach (var buff in entity.Buffs) _wire.AddBuff(entity, buff);
        }
        entity.Created = true;
        return true;
    }

    private void OpenGeneration(NeutralCampState state)
    {
        // Keep the previous generation through death callbacks and delayed
        // effects, then release its registry entries when the camp respawns.
        foreach (var netId in state.MutableMemberNetIds)
        {
            _members.Remove(netId);
            _uniqueNames.Remove(netId);
        }
        state.MutableMemberNetIds.Clear();
        _occupied.Clear();
        state.SpawnCycles++;
        state.Active = true;
        state.NextSpawnAt = null;
        state.PendingEmpty = false;
        state.BeginEmptyGeneration();
    }

    // Reconstruction (batch 219, kept by batch 248): the nearest legal cell for a member on a blocked scripted point; the script keeps
    // the scripted point, but sight from it is unrecoverable (see SpawnMember).
    private Position3 ResolveNearestLegalHome(int campId, int memberIndex, Position3 source,
        double radius, IReadOnlyList<(Position2 Position, double Radius)> occupied)
    {
        if (_navigation is null)
            return source;

        var sourcePoint = new Position2(source.X, source.Z);
        var sourceWirePoint = ClientPoint(sourcePoint);
        if (IsSafeSpawn(sourcePoint, radius, occupied) &&
            IsSafeSpawn(sourceWirePoint, radius, occupied))
            return source;

        var origin = _navigation.WorldToCell(sourcePoint);
        var candidates = new List<(Position2 Center, Position2 Wire, double DistanceSquared,
            int Column, int Row)>();
        for (var dc = -SpawnSearchRadiusCells; dc <= SpawnSearchRadiusCells; dc++)
        for (var dr = -SpawnSearchRadiusCells; dr <= SpawnSearchRadiusCells; dr++)
        {
            var cell = new NavCell(origin.Column + dc, origin.Row + dr);
            if (!_navigation.IsInBounds(cell)) continue;
            var center = _navigation.CellToWorld(cell);
            var wire = ClientPoint(center);
            var dx = center.X - sourcePoint.X;
            var dz = center.Z - sourcePoint.Z;
            candidates.Add((center, wire, dx * dx + dz * dz, cell.Column, cell.Row));
        }

        foreach (var candidate in candidates
            .OrderBy(value => value.DistanceSquared)
            .ThenBy(value => value.Column)
            .ThenBy(value => value.Row))
        {
            if (!IsSafeSpawn(candidate.Center, radius, occupied) ||
                !IsSafeSpawn(candidate.Wire, radius, occupied))
                continue;

            var wire = candidate.Wire;
            return new Position3(wire.X, _navigation.HeightAt(wire), wire.Z);
        }

        throw new InvalidOperationException(
            $"No terrain-safe neutral spawn near camp {campId} member {memberIndex + 1} at {sourcePoint}.");
    }

    private Position2 ClientPoint(Position2 point) =>
        Geometry.ClientWaypoints([point], new NavigationBounds(
            _navigation!.MinX, _navigation.MaxX, _navigation.MinZ, _navigation.MaxZ))[0];

    private bool IsSafeSpawn(Position2 point, double radius,
        IReadOnlyList<(Position2 Position, double Radius)> occupied)
    {
        if (_navigation is null || !_navigation.IsMovementWalkable(point) ||
            _navigation.PointClearance(point) < radius + SpawnClearanceMargin)
            return false;

        foreach (var other in occupied)
        {
            var dx = point.X - other.Position.X;
            var dz = point.Z - other.Position.Z;
            var required = radius + other.Radius + SpawnClearanceMargin;
            if (dx * dx + dz * dz < required * required)
                return false;
        }

        return true;
    }

    private int NearbyHeroLevel(Position2 position)
    {
        var radius2 = NeutralDefinitions.PlayerLevelRadius * NeutralDefinitions.PlayerLevelRadius;
        // HPByPlayerLevel iterates BBForEachUnitInTargetArea (range 9999, AffectHeroes), which does not visit a dead unit.
        return _world.Heroes()
            .Where(hero => hero.Alive && DistanceSquared(hero.Position, position) <= radius2)
            .Select(hero => hero.Level)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static double DistanceSquared(Position2 first, Position2 second)
    {
        var dx = first.X - second.X;
        var dz = first.Z - second.Z;
        return dx * dx + dz * dz;
    }
}
