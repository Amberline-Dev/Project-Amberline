using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Simulation.BehaviorTrees;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Minions;
using Amberline.Simulation.Stats;

namespace Amberline.Network.Sessions;

/// <summary>
/// Creates authoritative heroes first, then the preplaced Map1 objects. The
/// same entity instances are reused by every client synchronization burst.
/// </summary>
public sealed class SessionSpawnService
{
    public const byte MapNetNodeId = 0x40;
    public const uint PerceptionBubbleBase = 0x5000_0000u;
    public const float PerceptionTtl = 1.0e9f;

    private readonly WorldState _world;
    private readonly GameData _gameData;
    private readonly HeroStatsService _heroStats;
    private readonly ReplicationSyncCounter _syncIds;
    private readonly bool _visibilityBaseline;
    private readonly bool _minionFidelity;
    private ClassicHeroStatsSchema? _classicHeroStatsSchema;
    private readonly string _heroStatsMode;

    /// <param name="heroStatsMode">The mode string SynchVersion sends (<see cref="GameMaps.ModeOf"/>): the client's 0x4B collection is that mode's.</param>
    public SessionSpawnService(
        WorldState world,
        GameData gameData,
        HeroStatsService heroStats,
        ReplicationSyncCounter syncIds,
        bool visibilityBaseline = false,
        bool minionFidelity = false,
        string heroStatsMode = GameMaps.ClassicMode)
    {
        _heroStatsMode = heroStatsMode;
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        _heroStats = heroStats ?? throw new ArgumentNullException(nameof(heroStats));
        _syncIds = syncIds ?? throw new ArgumentNullException(nameof(syncIds));
        if (visibilityBaseline && minionFidelity)
            throw new ArgumentException("Visibility baseline and minion fidelity are mutually exclusive.");
        _visibilityBaseline = visibilityBaseline;
        _minionFidelity = minionFidelity;
    }

    public ReplicationSyncCounter SyncIds => _syncIds;

    /// <summary>
    /// Binds a map unit to its shipped character script as the unit is created (batch 159 step 2). The production
    /// composition installs the unit module's bind here; a check that builds map entities without it keeps the
    /// hand-written rules of the same unit.
    /// </summary>
    public Action<Entity, string>? BindUnitCharacter { get; set; }

    /// <summary>
    /// Batch 460: the ground height under a point (the match's navigation grid, installed by the production composition), the Y of the
    /// start camera; 0 for a check that builds the burst without a grid.
    /// </summary>
    public Func<Position2, double> GroundHeight { get; set; } = static _ => 0.0;

    /// <summary>Creates only the authoritative hero roster for loading tests.</summary>
    public IReadOnlyList<Entity> CreateHeroes(IReadOnlyList<SessionParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        if (participants.Count == 0)
            throw new ArgumentException("At least one participant is required.", nameof(participants));
        if (_world.Entities.Count != 0)
            throw new InvalidOperationException("Heroes can only be created on an empty world.");

        var seenPlayers = new HashSet<ulong>();
        foreach (var participant in participants)
        {
            ArgumentNullException.ThrowIfNull(participant);
            if (!seenPlayers.Add(participant.PlayerId))
                throw new InvalidOperationException($"Duplicate player ID {participant.PlayerId}.");
        }
        var heroes = new List<Entity>(participants.Count);
        var teamSizes = participants.GroupBy(member => member.Team)
            .ToDictionary(group => group.Key, group => group.Count());
        if (teamSizes.Values.Any(count => count > 5))
            throw new ArgumentException("Map1 supports at most five heroes per team.", nameof(participants));
        var teamIndices = new Dictionary<Team, byte>();
        foreach (var participant in participants)
        {
            participant.SpawnPositionIndex = teamIndices.GetValueOrDefault(participant.Team);
            teamIndices[participant.Team] = checked((byte)(participant.SpawnPositionIndex + 1));
            var hero = CreateHero(participant, teamSizes[participant.Team]);
            participant.Hero = hero;
            heroes.Add(hero);
        }
        return heroes;
    }

    public SessionWorldSnapshot CreateWorld(
        IReadOnlyList<SessionParticipant> participants,
        SessionRosterCounts? originalRosterCounts = null)
    {
        ArgumentNullException.ThrowIfNull(participants);
        if (participants.Count == 0)
            throw new ArgumentException("At least one participant is required.", nameof(participants));
        if (_world.Entities.Count != 0)
            throw new InvalidOperationException("A session world can only be created once on an empty world.");

        var heroes = CreateHeroes(participants);

        // Turret HP depends on the complete original roster, not on a later
        // disconnect or on the number of currently attached peers.
        var counts = originalRosterCounts ?? new SessionRosterCounts(
            heroes.Count(hero => hero.Team == Team.Order),
            heroes.Count(hero => hero.Team == Team.Chaos));
        var map = CreateMapEntities(counts);
        if (_visibilityBaseline)
            map = map.Append(CreateVisibilityProbe()).ToArray();
        _world.LocalHeroNetId = null;
        return new SessionWorldSnapshot(participants.ToArray(), heroes, map, counts);
    }

    public IReadOnlyList<Entity> CreateMapEntities(SessionRosterCounts originalRosterCounts)
    {
        if (!_world.Heroes().Any())
            throw new InvalidOperationException("Create the complete hero roster before map entities.");
        if (_world.Entities.Values.Any(entity => entity.UnitType is UnitType.Turret or UnitType.Structure))
            throw new InvalidOperationException("Map entities already exist.");

        var result = new List<Entity>(MapDefinitions.Turrets.Count +
                                      MapDefinitions.Nexuses.Count + MapDefinitions.Inhibitors.Count);
        for (var index = 0; index < MapDefinitions.Turrets.Count; index++)
        {
            var definition = MapDefinitions.Turrets[index];
            // Batch 599: the body radius is the turret character's GameplayCollisionRadius (every shipped turret character has 88.4).
            var radius = _gameData.Champion(MapDefinitions.TurretCharacter(definition.Tier, definition.Team)).CombatRadius;
            var entity = new Entity(_world.Allocate(UnitType.Turret), EntityKind.Turret,
                UnitType.Turret, definition.Team, definition.Position)
            {
                Name = definition.Name,
                ChampionName = "Annie",
                Radius = radius,
                CombatRadius = radius,
                BubbleId = definition.Team == Team.Order
                    ? PerceptionBubbleBase + (uint)index
                    : 0u,
            };
            var enemyCount = definition.Team == Team.Order
                ? originalRosterCounts.Chaos
                : originalRosterCounts.Order;
            TurretRules.Initialize(entity, enemyCount, _gameData, scriptOwned: BindUnitCharacter is not null);
            result.Add(_world.Register(entity));
            // Batch 159 step 2: the turret's own CharScript<turret>.luaobj runs at the character's creation (the rule
            // a hero gets), which is here — after Initialize, whose raw inibin values its permanents run on. Its
            // TurretBonusHealth one-shot supplies the health bonus and the perception bubble the DEV-06 stand-in below
            // adds by hand (BubbleSize 800 on the lane characters, 1000 on the nexus, 1600 on ChaosTurretShrine).
            if (BindUnitCharacter is not null)
                BindUnitCharacter(entity, MapDefinitions.TurretCharacter(definition.Tier, definition.Team));
            // DEV-06: ChaosTurretShrine supplies BubbleSize=1600 to
            // TurretBonusHealth. Its one-shot buff removes itself; the attached
            // true-sight bubble independently lasts 25000 seconds. Only the
            // unbound turret still needs the stand-in: Spells/TurretBonusHealth.luaobj
            // adds its own BBAddUnitPerceptionBubble as soon as the character runs.
            else if (entity.Team == Team.Chaos && VisibilityRules.IsShrine(entity))
                _world.TimedVisionBubbles.Add(new TimedVisionBubble(
                    PerceptionBubbleBase + (uint)index, entity.Team, entity.Position,
                    1600.0, 25000.0, RevealStealth: true, AnchorNetId: entity.NetId));
        }

        foreach (var definition in MapDefinitions.Nexuses)
        {
            var entity = new Entity(definition.NetId, EntityKind.Nexus, UnitType.Structure,
                definition.Team, definition.Position)
            {
                Name = definition.Name,
                ChampionName = "Annie",
                Radius = definition.Radius,
                CombatRadius = definition.Radius,
                PathfindingRadius = definition.PathfindingRadius,
                MaxHealth = definition.MaxHealth,
                Health = definition.MaxHealth,
                // Retail data: the map's Scene/CFG/ObjectCFG.cfg mBaseStaticHPRegen (20 for Summoner's Rift's HQ_T1 and HQ_T2, 15 on Map4), the
                // same per-second field the characters use (batch 49 settled the unit; CombatService.RegenerateMinionsAndStructures applies it).
                HealthRegen = definition.HealthRegen,
            };
            result.Add(_world.Register(entity));
        }

        foreach (var definition in MapDefinitions.Inhibitors)
        {
            var entity = new Entity(definition.NetId, EntityKind.Inhibitor, UnitType.Structure,
                definition.Team, definition.Position)
            {
                Name = definition.Name,
                ChampionName = "Annie",
                Radius = definition.AttackRadius,
                CombatRadius = definition.AttackRadius,
                PathfindingRadius = definition.Radius,
                MaxHealth = definition.MaxHealth,
                Health = definition.MaxHealth,
                // Retail data: ObjectCFG.cfg mBaseStaticHPRegen (15 for Summoner's Rift's six Barracks_* sections, 10 on Map4), per second.
                // Reconstruction: the maximum is the map's Constants.var bar_MaxHP (4000 on Summoner's Rift, 3000 on Map4), the only figure the
                // client's dampener bar reads (patch126-replication-tables.md, Building). The file's mMaxHP (3000 for the Order barracks, 5500
                // for the Chaos ones) is treated as leftover data by the owner (decision of 25 October 2026, overnight report item 3).
                HealthRegen = definition.HealthRegen,
            };
            result.Add(_world.Register(entity));
        }

        return result;
    }

    public IReadOnlyList<PlaintextPacket> BuildHeroSpawnBurst(
        SessionParticipant recipient,
        IReadOnlyList<SessionParticipant> participants,
        bool includeStartSpawn = true,
        bool revealNonRecipient = true,
        bool revealRecipient = true,
        bool includeChampionSpells = true,
        bool includeClassicHeroStats = true)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(participants);
        if (recipient.Hero is null || !participants.Contains(recipient))
            throw new InvalidOperationException("The recipient must be part of the created session world.");

        var packets = new List<PlaintextPacket>();
        if (includeStartSpawn)
        {
            // NOT RETAIL: retail counted server bots here, but our bots are roster players
            // for the batch-10 loading-card workaround, so they must not be counted twice.
            packets.Add(SpawnPackets.StartSpawn());
        }
        foreach (var member in participants)
        {
            var hero = member.Hero ?? throw new InvalidOperationException("Participant hero is not created.");
            // NOT RETAIL: retail flags every bot hero (byte 16) and the client names it from
            // game_bot_<skin>. Only the 18 retail bot champions have that key, so other bots go
            // out unflagged under the server name "<display name> Bot" (GameData.BotName).
            packets.Add(SpawnPackets.CreateHero(hero.NetId, member.ClientId, member.Name,
                hero.ChampionName, hero.Team == Team.Order, skinId: hero.SkinId, netNodeId: MapNetNodeId,
                spawnPositionIndex: member.SpawnPositionIndex,
                isBot: member.IsBot && _gameData.ClientNamesBot(hero.ChampionName)));
            // AvatarInfo runs the stock rune/mastery and summoner metadata
            // setup.  Publish it before the first authoritative hero stats
            // snapshot so the following 0xCC contains the effective values
            // after that client-side initialization.  Reconnects use this
            // same burst, so they do not need a later duplicate avatar row.
            packets.Add(VisionPackets.AvatarInfo(hero.NetId,
                member.SummonerSpell1, member.SummonerSpell2,
                member.Runes, member.Masteries, checked((byte)member.SummonerLevel)));
            // A restarted client needs the fixed roster identity before it
            // receives visibility. Keep hidden seat positions out of that
            // baseline; ordinary replication reveals them only when allowed.
            var reveal = member == recipient ? revealRecipient : revealNonRecipient;
            if (reveal)
            {
                packets.AddRange(VisionPackets.EnterVisibility(hero.NetId, hero.ChampionName, 0,
                    Float32(hero.MaxHealth), Float32(hero.Health), hero.Position, hero.Facing));
                packets.Add(BuildHeroStats(hero));
                if (includeClassicHeroStats)
                {
                    if (member == recipient)
                    {
                        // MatchStatistics.StartMatch has not run at the initial
                        // spawn boundary, so its authoritative duration origin
                        // is the stock zero value.
                        packets.Add(BuildClassicHeroStats(hero, now: 0.0, playerId: member.PlayerId));
                    }
                }
            }

            if (includeClassicHeroStats && member != recipient)
            {
                // Foreign rows receive only the four collection counters
                // proven to be read by the stock scoreboard. This follows the
                // identity packet even when position visibility is withheld;
                // private HeroStats fields stay at native defaults.
                packets.Add(BuildPublicClassicHeroStats(hero));
            }

            var champion = _gameData.Champion(hero.ChampionName);
            foreach (var (spellName, slot) in champion.SpellNames.Select((name, slot) =>
                         (hero.ActiveSpellNames.Count > slot &&
                              !string.IsNullOrWhiteSpace(hero.ActiveSpellNames[slot])
                              ? hero.ActiveSpellNames[slot] : name, slot)))
            {
                // A reconnect's live spell book follows model restoration in
                // its state burst. Avatar/loadout metadata below is needed now.
                if (!reveal || !includeChampionSpells || spellName.Length == 0) continue;
                packets.Add(SpellPackets.SetSpellData(hero.NetId,
                    ProtocolEncoding.HashString(spellName), checked((byte)slot)));
            }

            // SetSpellData does not assign trained ranks. Initialize the local
            // book with the same absolute rank/point packet used on reconnect.
            if (member == recipient && reveal && includeChampionSpells)
            {
                var points = checked((byte)Math.Clamp(hero.SkillPoints, 0, byte.MaxValue));
                for (var slot = 0; slot < Math.Min(hero.SpellRanks.Count, 4); slot++)
                    packets.Add(SpellPackets.UpgradeSpellAnswer(hero.NetId,
                        checked((byte)slot),
                        checked((byte)Math.Clamp(hero.SpellRanks[slot], 0, byte.MaxValue)),
                        points));
            }

            // Batch 460: the map's recall item and its spell (Mission.inibin ExtraSummonerItem*ItemID: 2001 Recall, 2005 OdinRecall on map 8).
            packets.Add(ItemsPackets.SetItem(hero.NetId, 6, checked((uint)_gameData.ExtraSummonerItemId), 1, 0));
            packets.Add(SpellPackets.SetSpellData(hero.NetId,
                ProtocolEncoding.HashString(_gameData.RecallSpellName), 10));
            if (reveal && member != recipient)
                packets.AddRange(VisionPackets.LeaveVisibility(hero.NetId));
        }

        if (_visibilityBaseline && recipient.Team == Team.Order)
        {
            var probe = _world.Entities.Values.FirstOrDefault(entity =>
                entity.Kind == EntityKind.Minion && entity.Name == "Minion_Basic" &&
                entity.Team == Team.Chaos && entity.Position == new Position2(9950.0, 4937.0));
            if (probe is not null)
            {
                packets.Add(SpawnPackets.SpawnMinion(new SpawnMinionSnapshot(
                    probe.NetId, MapNetNodeId, probe.Position,
                    TeamMapping.ToNative(probe.Team), probe.Name, probe.Name, probe.IsWard),
                    0.0));
                packets.Add(CombatPackets.MinionStats(probe.NetId,
                    new MinionStatsSnapshot(Float32(probe.Health), Float32(probe.AttackDamage),
                        Float32(probe.Speed), Float32(probe.Armor), Float32(probe.SpellBlock),
                        Float32(probe.AttackSpeedMod), Float32(probe.HealthRegen), 7),
                    _syncIds.Next(probe.NetId)));
            }
        }

        return packets;
    }

    public IReadOnlyList<PlaintextPacket> BuildMapReadyBurst(
        SessionParticipant recipient,
        IReadOnlyList<Entity> mapEntities)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(mapEntities);
        var packets = new List<PlaintextPacket>();
        foreach (var turret in mapEntities.Where(entity => entity.UnitType == UnitType.Turret))
            packets.Add(SpawnPackets.CreateTurret(turret.NetId, MapNetNodeId, turret.Name + "_A"));

        foreach (var turret in mapEntities.Where(entity => entity.UnitType == UnitType.Turret))
        {
            // A shrine is an obj_AI_Turret like the lane towers (one CreateTurret, the 16-field LOCAL1 table), and the client's first
            // replication of any unit carries every field (CReplData32 firstTime). The block used to be left out, so the client kept the
            // constructor defaults mBaseAttackDamage 25 and mPercentPhysicalDamageMod 0 against the server's 999 and 1 (real-client
            // telemetry, batch 67); it now gets the same TurretStats the lane towers get. Its protection state is the one the flags-only
            // packet used to carry: always protected (ProtectionRules: the fountain tier), untargetable to both teams.
            // Reconstruction: retail's packet set for a shrine is not recovered; this sends no EnterLocalVisibility (0xB5) for a shrine, since it would light a health bar that an untargetable tower never shows.
            // (NOTES.md recorded a stat block making the laser tower render destroyed in an early build; the destroyed skin is drawn only
            // for a missing or inactive child or CharacterState bit 13, none of which this block sets, but no native client has seen it.)
            // Batch 665: the shrine (untargetable, its character script's SetTargetable false) is VisibilityRules.IsShrine's, which takes the
            // Crystal Scar's four base shrines too (two of them end in "Shrine1" and got the 0xB5 that lit their bar).
            var shrine = VisibilityRules.IsShrine(turret);
            packets.Add(CombatPackets.TurretStats(turret.NetId,
                new TurretStatsSnapshot(Float32(turret.Health), Float32(turret.AttackDamage),
                    Float32(turret.MaxHealth), Float32(turret.Armor), Float32(turret.SpellBlock),
                    Float32(turret.HealthRegen), turret.Protected || shrine, Float32(turret.AttackSpeedMod),
                    Targetable: turret.Alive && !shrine),
                syncId: _syncIds.Next(turret.NetId)));
            if (shrine) continue;
            packets.Add(VisionPackets.EnterLocalVisibility(turret.NetId,
                Float32(turret.MaxHealth), Float32(turret.Health)));
        }

        packets.Add(SpawnPackets.EndSpawn());
        packets.Add(SpawnPackets.StartGame());
        var hero = recipient.Hero ?? throw new InvalidOperationException("Recipient hero is not created.");
        // Native camera interpolation divides distance by travel time. A tiny
        // positive duration snaps on the next frame, including zero-distance
        // Order starts, without producing 0/0 in the stock camera controller.
        packets.Add(CombatPackets.MoveCameraToPoint(hero.NetId,
            new Position3(hero.Position.X, GroundHeight(hero.Position), hero.Position.Z), travelTime: 0.001f));
        // The Dominion props (DominionProps): the stairs and, since batch 491, the two nexus crystals, all by name at their scene spawners,
        // on the Crystal Scar only. Each crystal's hold and gem follow its create.
        var odinProps = DominionProps.FromEnvironment();
        packets.AddRange(DominionProps.SpawnPackets(odinProps));
        if (odinProps != DominionPropFacing.Off)
        {
            var grid = NavigationGrid.Load(_gameData.Paths);
            // Batch 725: a reconnect after a nexus fell does not stand that crystal again (its fallen clip follows in the reconnect burst).
            packets.AddRange(DominionProps.CrystalStandPackets(new NavigationBounds(grid.MinX, grid.MaxX, grid.MinZ, grid.MaxZ),
                team => mapEntities.Any(entity => entity.Kind == EntityKind.Nexus && entity.Team == team && !entity.Alive)));
            // The gems' kill at a nexus fall (0x3D) names them, and the delivery routes a packet only to a client that holds what it names.
            recipient.View.Known.Add(DominionProps.CrystalGemNetId(Team.Order));
            recipient.View.Known.Add(DominionProps.CrystalGemNetId(Team.Chaos));
            // Batch 507: the stairs' lasers (0x8C) are bound to the stairs and aimed at the crystals, so the client must hold all four props.
            recipient.View.Known.UnionWith([DominionProps.OrderStairsNetId, DominionProps.ChaosStairsNetId,
                DominionProps.OrderCrystalNetId, DominionProps.ChaosCrystalNetId]);
        }
        foreach (var structure in mapEntities.Where(entity => entity.Kind is EntityKind.Nexus or EntityKind.Inhibitor))
        {
            packets.Add(CombatPackets.BuildingStats(structure.NetId,
                new BuildingStatsSnapshot(Float32(structure.Health), CombatPackets.SFlagTargetableToAll),
                syncId: _syncIds.Next(structure.NetId)));
        }
        packets.AddRange(BuildShopVisibility());
        return packets;
    }

    private Entity CreateHero(SessionParticipant participant, int teamSize)
    {
        var champion = _gameData.Champion(participant.ChampionName);
        _gameData.ValidateSkin(participant.ChampionName, participant.SkinId);
        var levelOne = _gameData.StatsAtLevel(champion, 1);
        var spawn = MapDefinitions.InitialHeroSpawn(participant.Team, teamSize,
            participant.SpawnPositionIndex);
        var hero = new Entity(_world.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero,
            participant.Team, spawn.Position)
        {
            Facing = spawn.Facing,
            ChampionName = participant.ChampionName,
            SkinId = participant.SkinId,
            Name = participant.Name,
            Gold = _gameData.LevelConstants.Values["ai_StartingGold"],
            Armor = levelOne.Armor,
            SpellBlock = levelOne.SpellBlock,
            Speed = levelOne.MoveSpeed,
            Health = levelOne.MaxHp,
            MaxHealth = levelOne.MaxHp,
            // The mana pool's Init(BaseMP, BaseMP) clamps a negative maximum to 0 (Vladimir's -10000, Dr. Mundo's -100000: no pool at all).
            Mana = Math.Max(0.0, levelOne.MaxMp),
            MaxMana = Math.Max(0.0, levelOne.MaxMp),
            Radius = champion.PathfindingRadius,
            CombatRadius = champion.CombatRadius,
            SelectionRadius = champion.SelectionRadius,
            AttackRange = levelOne.AttackRange,
            AttackDamage = levelOne.AttackDamage,
            HealthRegen = levelOne.HpRegen,
            ManaRegen = levelOne.MpRegen,
            AttackSpeedMod = levelOne.AttackSpeedMod,
            Protected = _minionFidelity,
            Runes = participant.Runes,
            Masteries = participant.Masteries,
            SummonerLevel = participant.SummonerLevel,
        };
        hero.SummonerSpells = [participant.SummonerSpell1, participant.SummonerSpell2];
        hero.MatchStatistics.RecordGoldEarned(hero.Gold);
        HeroAttackTimings.Apply(hero, _gameData);
        var registered = _world.Register(hero);
        // Batch 159 part 3: the team-size catch-up controller is Spells/ChampionChampionDelta.luaobj's own buff. A hero whose
        // champion has a character-script module gets the add from its CharOnActivate; a hero whose champion has none (the
        // ten hand-written ones) gets the same row through the match's unit-character module, so one path runs both
        // (ProductionMatchComposition.HeroCreated calls BbUnitCharacterModule.AddShippedBuff). Nothing is added here.
        // The first spawn stats packet must already include the selected
        // pages. Refresh after registration so the normal stats service owns
        // rune/mastery modifiers and initializes the combat hooks once.
        _heroStats.Refresh(registered);
        return registered;
    }

    private Entity CreateVisibilityProbe()
    {
        // The probe never takes a reward; the character base is all it needs.
        var stats = MinionDefinitions.BaseStats(MinionType.Melee, Team.Chaos);
        var probe = new Entity(_world.Allocate(UnitType.Minion), EntityKind.Minion,
            UnitType.Minion, Team.Chaos, new Position2(9950.0, 4937.0))
        {
            MinionType = MinionType.Melee,
            NodeId = MinionDefinitions.EnemyNodeId,
            Name = stats.CharacterName,
            Speed = stats.MoveSpeed,
            Radius = stats.Radius,
            CombatRadius = _gameData.Champion(MinionDefinitions.CharacterName(MinionType.Melee, Team.Chaos)).CombatRadius,
            Health = stats.MaxHealth,
            MaxHealth = stats.MaxHealth,
            AttackRange = stats.AttackRange,
            AttackDamage = stats.AttackDamage,
            AttackPeriod = stats.AttackPeriod,
            AttackWindup = stats.AttackWindup,
            AttackSpeedMod = stats.AttackSpeedMod,
            Armor = stats.Armor,
            SpellBlock = stats.SpellBlock,
            Created = false,
            Visible = false,
            LaneDone = true,
        };
        return _world.Register(probe);
    }

    private PlaintextPacket BuildHeroStats(Entity hero)
    {
        return CombatPackets.HeroStats(_heroStats.Snapshot(hero), _syncIds.Next(hero.NetId));
    }

    internal PlaintextPacket BuildClassicHeroStats(
        Entity hero, double now, ulong? playerId = null, string win = "")
    {
        ArgumentNullException.ThrowIfNull(hero);
        var schema = _classicHeroStatsSchema ??= ClassicHeroStatsSchema.Load(_gameData.Paths, _heroStatsMode);
        return ClassicHeroStatsPackets.HeroStats(hero.NetId, schema,
            ClassicHeroStatsSnapshot.Values(hero, schema, now, playerId, win));
    }

    internal PlaintextPacket BuildPublicClassicHeroStats(Entity hero)
    {
        ArgumentNullException.ThrowIfNull(hero);
        var schema = _classicHeroStatsSchema ??= ClassicHeroStatsSchema.Load(_gameData.Paths, _heroStatsMode);
        return ClassicHeroStatsPackets.HeroStats(hero.NetId, schema,
            ClassicHeroStatsSnapshot.PublicValues(hero, schema));
    }

    // The shops under their meshes' own names, the client's netids (orderShop01 on Summoner's Rift; OrderShop01 and chaosShop01 on Map4:
    // MapLayout.FromScene). The C2 marks the client's obj_Shop entered, which its picking needs (batch 704: a wrong id left it unclickable).
    private static IReadOnlyList<PlaintextPacket> BuildShopVisibility() =>
        [.. new[] { Team.Order, Team.Chaos }.Select(team => VisionPackets.EnterVisibilityClient(
            MapDefinitions.BuildingNetId(MapDefinitions.ShopName(team)), MapDefinitions.ShopName(team), 0, 0.0f, 0.0f,
            MapDefinitions.ShopPosition(team), new Position2(0.0, 1.0), isHero: false))];

    private static float Float32(double value)
        => ProtocolEncoding.Float32(value, nameof(value));
}
