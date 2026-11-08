namespace Amberline.Core;

public enum UnitType
{
    Hero = 0,
    Minion = 1,
    Turret = 2,
    Monster = 3,
    Structure = 4,
    Missile = 5,
}

public enum Team
{
    Order = 0,
    Chaos = 1,
    Neutral = 2,
}

public enum MinionType
{
    Melee = 0,
    Caster = 1,
    Siege = 2,
    Super = 3,
}

public enum EntityKind
{
    Unknown = 0,
    Hero,
    Minion,
    Turret,
    Inhibitor,
    Nexus,
    Monster,
    Neutral,
    Pet,
    Ward,
    ShacoBox,
    ShacoClone,
    YorickGhoul,
    YorickRevenant,
    NidaleeTrap,
    /// <summary>A Pet.lua clone of a hero that is neither Shaco's nor Yorick's (LeBlanc's image, Monkey King's decoy, Mordekaiser's ghost): <c>BBCloneUnitPet</c>.</summary>
    ClonePet,
}

public static class EntityKindExtensions
{
    public static EntityKind FromSourceType(string? sourceType)
    {
        return TryFromSourceType(sourceType, out var kind) ? kind : EntityKind.Unknown;
    }

    public static bool TryFromSourceType(string? sourceType, out EntityKind kind)
    {
        kind = sourceType?.ToLowerInvariant() switch
        {
            "hero" => EntityKind.Hero,
            "minion" => EntityKind.Minion,
            "turret" => EntityKind.Turret,
            "inhibitor" => EntityKind.Inhibitor,
            "nexus" => EntityKind.Nexus,
            "monster" => EntityKind.Monster,
            "neutral" => EntityKind.Neutral,
            "pet" => EntityKind.Pet,
            "ward" => EntityKind.Ward,
            "shaco_box" => EntityKind.ShacoBox,
            "shaco_clone" => EntityKind.ShacoClone,
            "yorick_ghoul" => EntityKind.YorickGhoul,
            "yorick_revenant" => EntityKind.YorickRevenant,
            "nidalee_trap" => EntityKind.NidaleeTrap,
            "clone_pet" => EntityKind.ClonePet,
            _ => EntityKind.Unknown,
        };
        return kind != EntityKind.Unknown || string.Equals(sourceType, "unknown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Pet.lua / YorickPHPet.lua actors that auto-attack: Tibbers, Shaco's clone, Yorick's revenant and his
    /// ghouls. Jack in the Box is a pet too, but it only casts a spell and never auto-attacks.
    /// </summary>
    public static bool IsAttackingPet(this EntityKind kind) =>
        kind is EntityKind.Pet or EntityKind.ShacoClone or EntityKind.YorickRevenant or EntityKind.YorickGhoul or EntityKind.ClonePet;

    public static string ToSourceType(this EntityKind kind) => kind switch
    {
        EntityKind.Hero => "hero",
        EntityKind.Minion => "minion",
        EntityKind.Turret => "turret",
        EntityKind.Inhibitor => "inhibitor",
        EntityKind.Nexus => "nexus",
        EntityKind.Monster => "monster",
        EntityKind.Neutral => "neutral",
        EntityKind.Pet => "pet",
        EntityKind.Ward => "ward",
        EntityKind.ShacoBox => "shaco_box",
        EntityKind.ShacoClone => "shaco_clone",
        EntityKind.YorickGhoul => "yorick_ghoul",
        EntityKind.YorickRevenant => "yorick_revenant",
        EntityKind.NidaleeTrap => "nidalee_trap",
        EntityKind.ClonePet => "clone_pet",
        _ => "unknown",
    };
}

public static class TeamMapping
{
    public static int ToNative(Team team) => team switch
    {
        Team.Order => 100,
        Team.Chaos => 200,
        Team.Neutral => 300,
        _ => throw new ArgumentOutOfRangeException(nameof(team)),
    };

    public static Team FromNative(int nativeTeam) => nativeTeam switch
    {
        100 => Team.Order,
        200 => Team.Chaos,
        300 => Team.Neutral,
        _ => throw new ArgumentOutOfRangeException(nameof(nativeTeam)),
    };

    public static bool TryFromNative(int nativeTeam, out Team team)
    {
        switch (nativeTeam)
        {
            case 100:
                team = Team.Order;
                return true;
            case 200:
                team = Team.Chaos;
                return true;
            case 300:
                team = Team.Neutral;
                return true;
            default:
                team = default;
                return false;
        }
    }
}

/// <summary>
/// Unmodified basic-attack timing retained so capped periods can be rebuilt
/// after a non-hero attack-speed buff is removed.
/// BasePeriod is the raw delay at multiplier 1.0; BaseAttackSpeed retains
/// permanent or copied modifiers before this entity's active buff pool.
/// </summary>
public readonly record struct AttackTimingBaseline(
    double BaseAttackSpeed,
    double BasePeriod,
    double CastFraction);

/// <summary>
/// Mutable authoritative unit state. Champion-specific pending state remains in
/// typed champion modules; this class only owns shared primitive state.
/// </summary>
public sealed partial class Entity
{
    public Entity()
    {
    }

    public Entity(uint netId, EntityKind kind, UnitType unitType, Team team, Position2 position)
    {
        NetId = netId;
        Kind = kind;
        UnitType = unitType;
        Team = team;
        Position = position;
    }

    public Entity(uint netId, string entityType, UnitType unitType, Team team, Position2 position)
        : this(netId, ParseSourceType(entityType), unitType, team, position)
    {
    }

    public uint NetId { get; set; }
    public string EntityType => Kind.ToSourceType();
    private EntityKind _kind;
    public EntityKind Kind
    {
        get => _kind;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _kind = value;
        }
    }
    public UnitType UnitType { get; set; }
    public Team Team { get; set; }
    public bool IsTrainingDummy { get; set; }
    public Position2 Position { get; set; }
    /// <summary>
    /// Batch 713: how far above the terrain the unit stands. Zero for every unit that walks; a unit a script spawns at a raised point keeps that
    /// point's height, which <c>BBGetUnitPosition</c> answers.
    /// Talon's blade markers stand where the outgoing blade ended, at the blade's flight height, and the returning blade leaves from there.
    /// </summary>
    public double Height { get; set; }
    public double Speed { get; set; }
    /// <summary>Hero flat movement modifier before percentages and soft caps.</summary>
    public double FlatMovementSpeedMod { get; set; }
    /// <summary>Hero flat health modifier (<c>mFlatHPPoolMod</c>): what <c>BBGetStat GetFlatHPPoolMod</c> reads. Set by the stat service.</summary>
    public double FlatHpPoolMod { get; set; }
    /// <summary>Hero flat cast range (<c>mFlatCastRangeMod</c>), added to every spell's range test. Set by the stat service.</summary>
    public double FlatCastRangeMod { get; set; }
    /// <summary>
    /// Unmodified NPC movement speed captured lazily before its first movement
    /// effect. Heroes rebuild from their loadout instead of using this value.
    /// </summary>
    public double? MovementSpeedBaseline { get; set; }
    public Position2? Target { get; set; }
    public List<Position2> Waypoints { get; set; } = new();
    public byte TeleportId { get; set; }
    public bool StealthHidden { get; set; }
    /// <summary>
    /// Latched client render state for an actor covered by the end-game
    /// minion no-render phase. This is an actor state, not a buff, so late
    /// visibility and reconnect snapshots retain the native CharacterState
    /// bit without creating a synthetic buff lifecycle.
    /// </summary>
    public bool NoRender { get; set; }
    public bool Visible { get; set; }

    /// <summary>
    /// Batch 673 (Reconstruction, batch 475 decision a): a unit every team sees, wherever it stands: a capture point's guardian, the point's
    /// structure, whose owner the ring and the minimap show to both teams (VisibilityRules.RevealsEntity). Its own sight stays its scripts'.
    /// </summary>
    public bool SeenByBothTeams { get; set; }
    public bool Created { get; set; } = true;
    public double Health { get; set; }
    public double MaxHealth { get; set; }
    public bool Alive { get; set; } = true;
    public double AttackCooldown { get; set; }
    public uint? AttackTarget { get; set; }
    public double[] AttackRevealRemaining { get; } = new double[3];
    /// <summary>Per-team fixed attack-reveal bubble centre (the attack point).</summary>
    public Position2[] AttackRevealPosition { get; } = new Position2[3];
    public uint? HelpTarget { get; set; }
    public double HelpRemaining { get; set; }
    public double HelpStickRemaining { get; set; }
    internal int HelpPriority { get; set; } = 9;
    internal uint? HelpStickTarget { get; set; }
    internal int HelpStickPriority { get; set; } = 9;
    public double HelpCallRemaining { get; set; }
    public double LaneHelpCallRemaining { get; set; }
    /// <summary>Destination retained while an attack-move order fights nearby enemies.</summary>
    public Position2? AttackMoveGoal { get; set; }
    /// <summary>Last server chase stop point sent for ATTACK_TO deduplication.</summary>
    public Position2? LastAttackStopPoint { get; set; }
    public double AttackRange { get; set; }
    public double AcquisitionRange { get; set; } = 450.0;
    public double AttackDamage { get; set; }
    public double MissileSpeed { get; set; }
    public double AttackWindup { get; set; }
    public double AttackPeriod { get; set; }
    public AttackTimingBaseline? AttackTimingBaseline { get; set; }
    public double RespawnAt { get; set; }
    public MinionType? MinionType { get; set; }
    public double GoldReward { get; set; }
    public double GlobalGoldReward { get; set; }
    /// <summary>
    /// Batch 500 (Reconstruction, BBRedirectGold): the hero that receives this unit's last-hit gold in place of its killer, or null.
    /// </summary>
    public Entity? GoldRedirectTarget { get; set; }
    public double ExperienceReward { get; set; }
    /// <summary>CharacterData GlobalExpGivenOnDeath: XP for every hero on the destroying team.</summary>
    public double GlobalExperienceReward { get; set; }
    public double Gold { get; set; }
    /// <summary>
    /// The gold the owner's client was last given, or null before any packet carried it. The HUD draws the whole
    /// figure (<c>floor(mGold)</c> into <c>GoldCurrent_%d</c>, no smoothing of its own), so a new gold reaches the
    /// client when that whole value moves and not on a timer.
    /// </summary>
    public double? ReplicatedGold { get; set; }
    /// <summary>Records that a packet carrying this gold just went to the owner's client.</summary>
    public void MarkReplicatedGold() => ReplicatedGold = Gold;
    /// <summary>The gold the owner's HUD draws: the whole figure of the replicated 32-bit value.</summary>
    public static double ShownGold(double gold) => Math.Floor((float)gold);
    public double Experience { get; set; }
    public int Level { get; set; } = 1;
    public int SkillPoints { get; set; } = 1;
    public double Mana { get; set; }
    public double MaxMana { get; set; }
    public List<int> SpellRanks { get; set; } = new() { 0, 0, 0, 0 };
    /// <summary>Live source spell names for transformed or recast forms.</summary>
    public List<string> ActiveSpellNames { get; set; } = new();
    public List<double> SpellCooldowns { get; set; } = new() { 0.0, 0.0, 0.0, 0.0 };
    public List<double> SummonerCooldowns { get; set; } = new() { 0.0, 0.0 };
    public List<double> ItemCooldowns { get; set; } = new() { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 };
    public bool CooldownsDisabled { get; set; }
    public bool UnlimitedResource { get; set; }
    public bool GodMode { get; set; }
    public string ChampionName { get; set; } = "Annie";
    public int SkinId { get; set; }
    public Position2 Facing { get; set; } = new(0.0, 1.0);
    public uint OwnerNetId { get; set; }
    public uint WardOwnerNetId { get; set; }
    public bool IsWard { get; set; }
    /// <summary>
    /// Rally's native beacon uses the ward wire flag for its spawn template,
    /// but is not a ward for hit-count damage or vision-provider behavior.
    /// </summary>
    public bool IsSummonerBeacon { get; set; }
    public bool RevealsStealth { get; set; }
    public double ExpiresAt { get; set; }
    public string AiState { get; set; } = "march";
    public List<Position2> LaneWaypoints { get; set; } = new();
    public bool LaneDone { get; set; }
    public double? DeathTime { get; set; }
    public double Radius { get; set; }
    private double? _combatRadius;
    /// <summary>Gameplay radius for attacks and line spells (skin scale 1); structures default to their attack geometry.</summary>
    public double CombatRadius
    {
        get => _combatRadius ?? (UnitType is UnitType.Turret or UnitType.Structure ? Radius : 65.0);
        set => _combatRadius = value;
    }
    /// <summary>Structure pathing geometry; other units retain their existing Radius footprint.</summary>
    public double? PathfindingRadius { get; set; }
    /// <summary>
    /// CharacterData SelectionRadius (the client's mOverrideCollisionRadius),
    /// which sizes the IsInWallOfGrass brush test. Negative when absent.
    /// </summary>
    public double SelectionRadius { get; set; } = -1.0;
    private bool? _pathingFlag290;
    /// <summary>
    /// The actor's hero flag. Retail sets it from the actor creation's flag argument, which the base unit's load fills with the
    /// hero type bit (bit 12 of the object's type word, set by the Hero constructor): every hero is flagged. A flagged actor plans with
    /// the 10000-iteration A* and its weights, gives up collisions at 15 stuck frames, and moves with 0.2 of its radius.
    /// Nothing else is flagged: a pet, clone or ghoul is a plain <c>obj_AI_Minion</c> (type bit 0x800, set when the pet is spawned),
    /// and the minion's creation writes only a field of its owner, not the actor's byte
    /// (audit A5, 18 October 2026; batch 61 had inferred otherwise).
    /// </summary>
    public bool PathingFlag290
    {
        get => _pathingFlag290 ?? UnitType == UnitType.Hero;
        set => _pathingFlag290 = value;
    }
    /// <summary>
    /// Body collision cannot be bypassed by ghosting or stuck release: the stored flag, or a buff whose script set <c>SetGhostProof</c>
    /// (CharacterState bit 11, the actor's ghost-proof flag).
    /// </summary>
    public bool GhostProof { get => _ghostProof || AnyBuffOrStatus(buff => buff.Payload.GhostProof); set => _ghostProof = value; }
    private bool _ghostProof;
    /// <summary>
    /// The IgnoreCollision bit of the unit's SpawnMinion (0x80, the flag byte's bit 0). The client's 0x80 case passes it to
    /// the minion's creation (argument 10), which hands it to the object creation as its flags; flag 1 sets the object's
    /// bit 0 of its flag word, and the unit's update passes that bit to the actor update as its third argument, which only
    /// skips the stopped actor's put-on-floor at the end of the update. Heroes, turrets, neutrals (0x66) and pets are created with flags 0.
    /// </summary>
    public bool SpawnIgnoreCollision { get; set; }
    public Position2 SeparationAccumulation { get; set; }
    /// <summary>
    /// The client actor's stored last final frame velocity (Actor_Common +0x8c/+0x94): the displacement of its
    /// last update, decayed by 0.75 each update while stopped. Neighbours read it for their avoidance.
    /// </summary>
    public Position2 FrameVelocity { get; set; }
    public double StuckElapsed { get; set; }
    public int RepathAttempts { get; set; }
    public int RepathCount { get; set; }
    public double LastAnchorTime { get; set; }
    public List<Position2> AnchorPath { get; set; } = new();
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Reconstruction (batch 476d): the minimap icon the encounter names for this unit (its EncounterMonster MinimapIcon, for example
    /// CenterRelicLeft), or empty. It travels in the neutral creation packet (0x66, CreateNeutral's minimap icon field) for a camp icon
    /// (<see cref="MinimapCamp"/>), else in 0xE1 UnitSetMinimapIcon (batch 511); the client draws the icon from its minimap.ini section.
    /// </summary>
    public string MinimapIcon { get; set; } = string.Empty;

    /// <summary>
    /// Batch 511: the client camp (the 0x66 GroupNumber) that draws <see cref="MinimapIcon"/>, or 0 when the unit draws the icon itself
    /// (0xE1 UnitSetMinimapIcon). The client keeps one NeutralMinionCamp per group number and gives it the icon of the first 0x66 of that
    /// number, so each icon place needs its own number. Set at spawn (EncounterService).
    /// </summary>
    public int MinimapCamp { get; set; }

    /// <summary>
    /// Batch 499: what a minion may do with this unit, from its character file's <c>Useable</c> section (the useable component):
    /// the <c>MinionUseSpell</c> and that spell's <c>CastRange</c>, and which side may use it (<c>AllyCanUse</c>, <c>EnemyCanUse</c>; a neutral
    /// unit is the other side's). Null when the unit is not useable by minions (<c>MinionUseable</c> 0, or no MinionUseSpell). Set at spawn.
    /// </summary>
    public (string Spell, double Range, bool AllyCanUse, bool EnemyCanUse)? MinionUse { get; set; }
    /// <summary>
    /// The shipped character script bound to this unit (<see cref="T:Amberline.Simulation.Spells.Bb.BbUnitCharacterModule"/>),
    /// or null while the unit keeps the hand-written path. A turret rule the character scripts state checks it and stands
    /// down, so one unit never carries both models.
    /// </summary>
    public string? BoundCharacter { get; set; }
    public uint BubbleId { get; set; }
    /// <summary>NPC defenses before active buffs, including permanent growth.</summary>
    public (double Armor, double SpellBlock)? ResistanceBaseline { get; set; }
    /// <summary>
    /// NPC penetration before its character script's sample and its buffs (a hand-written turret seed, a clone's copy), taken at the
    /// first recompute (<c>BuffLifecycle.RecomputeNpcPenetration</c>).
    /// </summary>
    public (double ArmorFlat, double ArmorPercent, double MagicFlat, double MagicPercent)? PenetrationBaseline { get; set; }
    public double Armor { get; set; }
    public double SpellBlock { get; set; }
    public double TurretBaseDamage { get; set; }
    public double TurretBaseArmor { get; set; }
    public double TurretBaseSpellBlock { get; set; }
    public double TurretScalingStart { get; set; }
    public double TurretScalingDuration { get; set; }
    public int TurretScalingStacks { get; set; }
    public double TurretLastRegenSend { get; set; } = -1.0;
    public double HealthRegen { get; set; }
    /// <summary>
    /// <c>SetBuildingHealthRegenEnabled</c>: whether this building regenerates at all. The
    /// end-of-game ceremony switches both HQs off (EndOfGame.txt proto 0.0 pc 0026-0044) — retail
    /// sets the property on the object, so the flag is the object's own, not a match-wide switch.
    /// </summary>
    public bool BuildingHealthRegenEnabled { get; set; } = true;
    public double ManaRegen { get; set; }
    public double AttackSpeedMod { get; set; } = 1.0;
    public double AbilityPower { get; set; }
    public double CritChance { get; set; }
    public double CritMultiplier { get; set; } = 2.0;
    public double Dodge { get; set; }
    public double CooldownMod { get; set; }
    public double Hardness { get; set; }
    public bool DodgePiercing { get; set; }
    // Odyn's Veil kept its stored damage on the hero while the item active was hand-written; the shipped
    // Spells/OdynsVeil.luaobj keeps it in the window buff's own CharVars.StoredDamage (batch 149 step 4).
    public double ArmorPenetrationFlat { get; set; }
    public double ArmorPenetrationPercent { get; set; }
    public double MagicPenetrationFlat { get; set; }
    public double MagicPenetrationPercent { get; set; }
    public double LifeSteal { get; set; }
    public double SpellVamp { get; set; }
    public int CampId { get; set; }
    public int NeutralMember { get; set; }
    public Position3? NeutralHome { get; set; }
    public Position3? NeutralGroupPosition { get; set; }
    public Position3? NeutralFacePosition { get; set; }
    public string NeutralState { get; set; } = "idle";
    public int NeutralLeashCounter { get; set; }
    public double NeutralRetreatElapsed { get; set; }
    public double NeutralRegenElapsed { get; set; }
    public Position2? NeutralLastMove { get; set; }
    public int NeutralPlayerLevel { get; set; }
    public double NeutralScalingElapsed { get; set; }
    public double ForcedMoveGrace { get; set; }
    public bool Protected { get; set; }

    /// <summary>
    /// NOT RETAIL (batch 671): where this hero respawns instead of its team's spawn point (retail's respawn location is the fountain). Null
    /// for every hero but the Basic Tutorial's Master Yi (the owner's decision of 3 November 2026, LevelScriptHost.ChampionBattleMidway).
    /// </summary>
    public Position2? RespawnPoint { get; set; }

    /// <summary>
    /// Batch 457: a server turret whose client object is a minion of its character. The stock client makes no turret of a packet: a CreateTurret
    /// (0xA5) binds its net id to the turret of that name in the map's scene, and any other name trips the client's turret check
    /// and binds nothing. The Odin capture-point turrets have no scene object, so the clients get them as minions
    /// (0x80 and the minion stat blocks), the only unit the client creates from a spawn packet; the server runs them as turrets. Reconstruction:
    /// retail's packets for them are not recovered.
    /// </summary>
    public bool ReplicatedAsMinion { get; set; }

    /// <summary>A turret whose client object is a turret of the map's scene: created by CreateTurret, replicated with the turret blocks.</summary>
    public bool IsSceneTurret => (Kind == EntityKind.Turret || UnitType == UnitType.Turret) && !ReplicatedAsMinion;
    /// <summary>
    /// The protection state the clients were last sent for this structure (ProtectionRules owns it). The level script's natives
    /// write <see cref="Protected"/> before the ladder runs, so that flag is not what the clients hold; the ladder sends whenever
    /// this differs from the state it computes.
    /// </summary>
    public bool ProtectionSent { get; set; }
    /// <summary>
    /// A script's placemarker (<c>BBSpawnMinion</c> with <c>Placemarker=true</c>: Morgana's HiddenMinion, Janna's TestCube, Baron's geyser, Orianna's ball):
    /// registered so scripts can address, face, buff and cast from it. The spawn packet has no placemarker bit (the client's 0x80 case reads
    /// none), so a placemarker is announced like any unit (<see cref="Announced"/>), with its no-render state on the replicated action state. Orianna's
    /// ball is the exception: the clients see it through its own projection (<c>ReplicationDelivery</c> ball projections), not the entity paths.
    /// </summary>
    public bool Placemarker { get; set; }
    /// <summary>
    /// The unit's spawn packet (<c>SpawnMinion</c>) was sent to the clients, so the entity paths serve it as a minion (creation, stats, visibility,
    /// buffs, retirement). Set for every script spawn except Orianna's ball, which the ball projection announces.
    /// <see cref="Created"/> cannot tell this apart: the visibility refresh sets it to "some observer knows the unit".
    /// </summary>
    public bool Announced { get; set; }
    /// <summary>
    /// The <c>VisibilitySize</c> a script gave this unit in <c>BBSpawnMinion</c> (0: none). Reconstruction: it is the sight radius the unit gives its own
    /// team for as long as it lives (Talon's blood drops 450, Lux's and Xerath's beam cubes 300 to 450, Galio's Righteous Gust 100); the stock
    /// client only stores it from the spawn packet (<c>SpawnMinion.visibilitySize</c> into the unit's own field) and never reads it,
    /// so the server is the only user. <c>VisibilityRules.VisibilitySizeRange</c> reads it as a second sight bubble beside the unit's
    /// CharacterData bubble (batch 310: the minion's creation makes that one whatever the size).
    /// </summary>
    public double VisibilitySize { get; set; }
    /// <summary>
    /// Monotonic: this structure has been observed dead at least once. LevelScript
    /// HandleDestroyedObject releases HQ towers and the nexus permanently, so a
    /// dampener respawn must not re-protect them (see ProtectionRules).
    /// </summary>
    public bool StructureFell { get; set; }

    // Inventory, buffs and champion state live in the typed partial files and
    // their owning modules; keep dynamic/object placeholders out of this state.

    private static EntityKind ParseSourceType(string sourceType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);
        if (EntityKindExtensions.TryFromSourceType(sourceType, out var kind)) return kind;
        throw new ArgumentException($"Unknown entity source type '{sourceType}'", nameof(sourceType));
    }
}
