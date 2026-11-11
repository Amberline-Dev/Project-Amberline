using System.Collections.ObjectModel;
using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Ai;

namespace Amberline.Simulation.Neutral;

/// <summary>
/// One neutral camp, the engine's view of one <c>NeutralMinionCamps</c> entry of the match's map's shipped
/// <c>NeutralMinionSpawn.luaobj</c>. <see cref="InitialDelay"/> is the script's
/// <c>GroupDelaySpawnTime</c> (the wait before the first generation) and <see cref="RespawnDelay"/>
/// the camp's respawn (<c>GroupsRespawnTime</c>, or <c>RespawnTime</c> on Map4); both are read out of the script
/// and kept under the names the engine's scheduling has always used for them. <see cref="Groups"/> keeps every
/// group; the script's roll picks one per generation.
/// </summary>
public sealed record CampDefinition(
    int Number,
    IReadOnlyList<IReadOnlyList<string>> Groups,
    IReadOnlyList<Position3> SpawnPoints,
    IReadOnlyList<Position3> FacePoints,
    double InitialDelay,
    double RespawnDelay,
    int Level = 1)
{
    public Position3 GroupPosition => SpawnPoints[0];
    /// <summary>The first listed group: the whole camp on Summoner's Rift, where every camp lists one.</summary>
    public IReadOnlyList<string> Roster => Groups.Count > 0 ? Groups[0] : Array.Empty<string>();
}

/// <summary>Neutral values calculated from the shipped champion and basic attack data.</summary>
public sealed record NeutralStats(
    double Hp,
    double AttackDamage,
    double Armor,
    double SpellBlock,
    double HpRegen,
    double Speed,
    double AttackRange,
    double AcquisitionRange,
    double Radius,
    double GameplayRadius,
    double AttackSpeedMod,
    double AttackPeriod,
    double Windup,
    double MissileSpeed,
    double GoldReward,
    double GlobalGoldReward,
    double ExperienceReward,
    double HealthBonus,
    double DamageBonus,
    int PlayerLevel,
    double SpawnTime)
{
    /// <summary>CharacterData GlobalExpGivenOnDeath (Worm 900; Dragon 0).</summary>
    public double GlobalExperienceReward { get; init; }
    /// <summary>The record's Data*BaseStaticMPRegen (0.5 for the camp monsters, 0 for the Worm), replicated as mPARRegenRate.</summary>
    public double MpRegen { get; init; }
    public double CritChance { get; init; }
    public double CritMultiplier { get; init; } = 2.0;
}

/// <summary>One active monster and its immutable spawn-time values.</summary>
public sealed record NeutralMember(
    Entity Entity,
    string Name,
    int CampId,
    int Member,
    Position3 Home,
    Position3 GroupPosition,
    Position3 FacePosition,
    NeutralStats Stats)
{
    public uint NetId => Entity.NetId;
}

/// <summary>State machine state for one camp, including the pending empty edge.</summary>
public sealed class NeutralCampState
{
    internal NeutralCampState(CampDefinition definition)
    {
        Definition = definition;
        CurrentGroupPosition = definition.GroupPosition;
    }

    public CampDefinition Definition { get; }
    /// <summary>
    /// The group anchor used by the active generation: the scripted point of its first member (batch 219, no
    /// terrain search). Creation and empty-camp visibility use it.
    /// </summary>
    public Position3 CurrentGroupPosition { get; internal set; }
    public double? NextSpawnAt { get; internal set; }
    public double HealthBonus { get; internal set; }
    public double DamageBonus { get; internal set; }
    public IReadOnlyList<uint> MemberNetIds => _memberNetIds;
    public bool Active { get; internal set; }
    public int SpawnCycles { get; internal set; }
    public bool PendingEmpty { get; internal set; }
    /// <summary>
    /// Teams that have learned this generation is empty.  This is deliberately
    /// separate from each observer's delivery cursor: every eligible teammate
    /// must receive its own 0xCB packet, while the other team remains unaware.
    /// </summary>
    public IReadOnlySet<Team> EmptyObservedTeams => _emptyObservedTeams;

    internal List<uint> MutableMemberNetIds => _memberNetIds;
    internal void BeginEmptyGeneration()
    {
        _emptyObservedTeams.Clear();
    }

    internal void MarkEmptyObserved(Team team)
    {
        if (team is Team.Order or Team.Chaos)
            _emptyObservedTeams.Add(team);
    }

    private readonly List<uint> _memberNetIds = new();
    private readonly HashSet<Team> _emptyObservedTeams = new();
}

/// <summary>Constants and data-backed calculation for the neutral roster.</summary>
public static class NeutralDefinitions
{
    public const int NeutralWireTeam = 300;
    public const byte NeutralNodeId = 0x40;
    // Reconstruction: NeutralMinionSpawn.luaobj stores only each camp's raw GroupDelaySpawnTime (0/15/50/800 s)
    // and the separate SpawnInitialNeutralMinions callback names no base timer. 100 s is inferred: every raw delay
    // plus 100 s equals the 2011 HUD first-spawn times (1:40 camps, 1:55 buffs, 2:30 Dragon, 15:00 Baron; see
    // docs/minions/patch126-spawn-schedules.md). Respawns use the raw delay without this base.
    public const double InitialSpawnSeconds = 100.0;
    public const double LeashRadius = 850.0;
    public const double LeashProtectionRadius = 750.0;
    public const int LeashRetryLimit = 2;
    public const double RetreatCheckSeconds = 0.5;
    // Leashed.luaobj FEAR_WANDER_DISTANCE and InitTimer("TimerFeared", 0.5).
    public const double FearWanderDistance = 500.0;
    public const double FearWanderSeconds = 0.5;
    public const double RetreatRegenSeconds = 1.0;
    public const double RetreatRegenPercent = 0.10;
    public const double RetreatArrivalRadius = 100.0;
    public const double PlayerLevelRadius = 9999.0;
    public const double PlayerLevelCheckSeconds = 10.0;

    public static readonly IReadOnlyDictionary<string, double> PlayerLevelHealth =
        new ReadOnlyDictionary<string, double>(new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["AncientGolem"] = 175.0,
            ["LizardElder"] = 175.0,
            ["Dragon"] = 220.0,
        });

    /// <summary>
    /// NeutralMinionSpawn's <c>DefaultNeutralMinionValues</c> experience column — the amount its
    /// <c>NeutralMinionDeath</c> hands to <c>GiveExpToNearHeroesFromNeutral</c>. The script's
    /// gold and soul columns are zero for every monster; the gold a neutral pays comes from its
    /// shipped CharacterData.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, double> MonsterExperience = MapScriptData.NeutralExperience;

    /// <summary>
    /// The match's map's camps (Summoner's Rift's twelve by default), read out of its shipped <c>NeutralMinionSpawn.luaobj</c>'s
    /// <c>NeutralMinionCamps</c> (its <c>NeutralMinionInit</c> fills the roster, the
    /// <c>Make3DPoint</c> spawn and face points, <c>GroupDelaySpawnTime</c>,
    /// the respawn field (<c>GroupsRespawnTime</c>, or <c>RespawnTime</c> on Map4) and <c>CampLevel</c>). A match reads its own map through <see cref="CampsFor"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, CampDefinition> Camps = CampsFor(GameData.Default.Paths);

    /// <summary>
    /// The camps of one map, read out of that map's own <c>NeutralMinionSpawn.luaobj</c>: Summoner's Rift has twelve, the Harrowing rift
    /// (LEVELS/Map2) thirteen (camp 13 is the Urf, batch 434). The camp numbers and every timer are the script's own.
    /// </summary>
    public static IReadOnlyDictionary<int, CampDefinition> CampsFor(GameDataPaths paths) =>
        BuildCamps(MapScriptData.NeutralCampsFor(paths));

    /// <summary>The experience column of one map's own <c>DefaultNeutralMinionValues</c> (the Harrowing rift pays the Urf 50, batch 434).</summary>
    public static IReadOnlyDictionary<string, double> MonsterExperienceFor(GameDataPaths paths) =>
        MapScriptData.NeutralExperienceFor(paths);

    /// <summary>
    /// The name <c>InitializeNeutralMinionInfo</c> stores in <c>UniqueNames</c>
    /// (NeutralMinionSpawn.luaobj proto 0.2): the monster name followed by the camp, group and
    /// member indices. <c>NeutralMinionDeath</c> matches its argument against that table, so this
    /// is the identity the engine must report a death under.
    /// </summary>
    public static string UniqueName(string monsterName, int camp, int group, int member) =>
        $"{monsterName}{camp}.{group}.{member}";

    /// <summary>
    /// Reconstruction: the minimap icon is client presentation and NeutralMinionSpawn names none, so it follows the monster. Dragon
    /// (Summoner's Rift camp 6), Worm (Baron Nashor, camp 12) and blueDragon (The Twisted Treeline camp 7) take the Epic icon; the other
    /// monsters keep the camp icon. The tooltips also call AncientGolem and LizardElder "Epic Monster" (fontconfig_en_US.txt:1847, 1894),
    /// and they stay Camp as they always have. The two icons are minimap.ini's [Epic] and [Camp] (DATA/Menu/HUD/minimap.ini:166-183).
    /// </summary>
    public static string MinimapIcon(string monsterName) => monsterName is "Dragon" or "Worm" or "blueDragon" ? "Epic" : "Camp";

    private static IReadOnlyDictionary<int, CampDefinition> BuildCamps(IReadOnlyList<LevelNeutralCamp> neutralCamps)
    {
        var camps = new Dictionary<int, CampDefinition>();
        foreach (var camp in neutralCamps)
            camps[camp.Number] = new CampDefinition(camp.Number, camp.Groups, camp.Positions, camp.FacePositions,
                camp.GroupDelaySpawnTime, camp.GroupsRespawnTime, camp.CampLevel);
        return new ReadOnlyDictionary<int, CampDefinition>(camps);
    }
}

public static class NeutralStatsCalculator
{
    public static NeutralStats Calculate(GameData gameData, string name,
        double healthBonus = 0.0, double damageBonus = 0.0, int playerLevel = 0,
        double spawnTime = 0.0)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!double.IsFinite(healthBonus) || !double.IsFinite(damageBonus) ||
            !double.IsFinite(spawnTime) || playerLevel < 0)
            throw new ArgumentOutOfRangeException(nameof(healthBonus));

        var character = gameData.Champion(name);
        // Reconstruction: a monster with no shipped <name>BasicAttack flies no basic-attack missile, the same rule as the engine's
        // AttackMissileSpeed. Urf ships none (no UrfBasicAttack): its CharOnActivate sets SetCanAttack false, so it never swings, and
        // its slot-1 spell is UrfSpell (Data*Spell1), which the script casts at Warwick.
        // Batch 741: and the client flies a missile only for CastType 1, 2 and 3 (DoCast_ClientAndServer: case 0 runs the effect at once),
        // the rule a hero's, a turret's and a spawned unit's attack already read. AncientGolem, LizardElder, TwistedLizardElder and Lizard
        // ship CastType 0 attacks with MissileSpeed 500: read as a flight, their swing landed ~0.4 s after its cast frame, after the death
        // of a monster killed in between, without its CharOnHitUnit (b739: AncientGolem 50.00 instead of 71.50).
        var basicAttack = name + "BasicAttack";
        var missileSpeed = gameData.HasSpell(basicAttack) &&
            Amberline.Simulation.Spells.Bb.BbChampionModule.CastType(gameData.Spell(basicAttack)) is 1 or 2 or 3
            ? gameData.Spell(basicAttack).MissileSpeed : 0.0;
        // CharacterData's legacy Data*AttackSpeed field is not the live
        // attack-speed multiplier.  The native CharacterData loader reads
        // the delay offsets and initializes mAttackSpeedMod to 1.0; later
        // attack-speed buffs/slow effects change the entity value.  The raw
        // field is present in several neutral assets (1.5, and 1.6 for
        // Worm), but using it here makes every unbuffed camp attack too fast.
        const double attackSpeedMod = 1.0;
        var delayOffset = Number(character.Raw, "Data*AttackDelayOffsetPercent", 0.0);
        var castOffset = Number(character.Raw, "Data*AttackDelayCastOffsetPercent", 0.0);
        // Dragon and Worm omit GoldGivenOnDeath, so their killer receives the
        // native CharacterData default of 25 on top of the global team gold.
        var gold = character.GoldGivenOnDeath;
        var globalGold = character.GlobalGoldGivenOnDeath;
        var period = Math.Clamp(1.6 * (1.0 + delayOffset) / Math.Max(gameData.LevelConstants.AttackSpeedMultiplierFloor, attackSpeedMod), 0.4, 5.0);
        // The Worm's spawn-time health (2.083 per game second) and regeneration (0.00625 per game second) are
        // Spells/ResistantSkin.luaobj's OnBuffActivate (batch 174 part 2): CharScriptWorm adds that buff when the unit binds.
        var regen = character.HpRegen;
        var hp = character.BaseHp + healthBonus +
            (NeutralDefinitions.PlayerLevelHealth.TryGetValue(name, out var perLevel) ? perLevel * playerLevel : 0.0);
        var xp = NeutralDefinitions.MonsterExperienceFor(gameData.Paths).TryGetValue(name, out var value) ? value : 0.0;
        // Native basic attacks derive cast delay from the entity's effective
        // period and its CharacterData cast offset. Worm's scripted attacks
        // use their own spell/action timers and do not enter this path.
        var neutralAttackWindup = period * Math.Max(0.0, 0.3 + castOffset);
        return new NeutralStats(hp, character.BaseDamage + damageBonus, character.Armor,
            character.SpellBlock, regen, character.MoveSpeed, character.AttackRange,
            // The character's Data*AcquisitionRange as the CharacterData loader reads it (Golem 300, Dragon 500, Worm 400,
            // 750 when the key is absent). The leash radius is Leashed.luaobj's own LEASH_RADIUS, not an acquisition range.
            character.AcquisitionRange, character.PathfindingRadius, character.GameplayRadius,
            attackSpeedMod, period, neutralAttackWindup, missileSpeed, gold, globalGold,
            xp, healthBonus, damageBonus, playerLevel, spawnTime)
        {
            GlobalExperienceReward = character.GlobalExpGivenOnDeath,
            MpRegen = character.MpRegen,
            CritChance = Number(character.Raw, "Data*BaseCritChance", 0.0) / 100.0,
            CritMultiplier = Number(character.Raw, "Data*CritDamageBonus", 2.0),
        };
    }

    private static double Number(InibinData data, string key, double fallback)
    {
        if (!data.TryGetValue(InibinReader.KeyHash(key), out var value)) return fallback;
        if (value.Kind == InibinValueKind.Boolean) return value.Boolean ? 1.0 : 0.0;
        return value.IsNumeric ? value.AsDouble() : fallback;
    }
}
