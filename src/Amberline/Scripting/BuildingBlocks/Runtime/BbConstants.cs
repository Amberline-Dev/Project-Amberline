namespace Amberline.Scripting.BuildingBlocks.Runtime;

// The symbolic constants BuildingBlocks scripts reference. Every name and value is the registration the stock
// client makes in its registration block; the scripts see them as plain Lua
// numbers, so the runtime keeps them as numbers (aliases such as BUFF_Silence = BUFF_Pacified = BUFF_Disarm stay equal,
// as in retail) and the enums give hosts a typed view: (BbDamageType)(int)value. Member names are the script identifiers
// verbatim, which is why they are upper snake case.
// Generated from the exe registrations; the corpus check scripting.bb-runtime-constants keeps the table in step
// with every reference the shipped scripts make.

/// <summary>Target-selection flags of spells and unit scans (SPELL_* and the UNITSCAN_* aliases).</summary>
[Flags]
public enum BbSpellFlags : uint
{
    SPELL_AffectBuildings = 0x1000,
    SPELL_AffectEnemies = 0x400,
    SPELL_AffectFriends = 0x800,
    SPELL_Autocast = 0x2,
    SPELL_Instacast = 0x4,
    SPELL_NotAffectSelf = 0x2000,
    SPELL_PersistThroughDeath = 0x8,
    SPELL_NonDisspellable = 0x10,
    SPELL_AffectDead = 0x80000,
    SPELL_AffectHeroes = 0x10000,
    SPELL_AffectUntargetable = 0x200,
    SPELL_AffectMinions = 0x8000,
    SPELL_AffectNeutral = 0x4000,
    SPELL_AffectNotPet = 0x100000,
    SPELL_AffectTurrets = 0x20000,
    SPELL_AlwaysSelf = 0x40000,
    SPELL_AffectBarrackOnly = 0x200000,
    SPELL_NonTargetableAlly = 0x800000,
    SPELL_NonTargetableEnemy = 0x1000000,
    SPELL_NonTargetableAll = 0x1800000,
    SPELL_AffectUseable = 0x8000000,
    UNITSCAN_Enemies = 0x400,
    UNITSCAN_Friends = 0x800,
    UNITSCAN_Buildings = 0x1000,
    UNITSCAN_Dead = 0x80000,
}

/// <summary>How a buff add stacks or refreshes.</summary>
public enum BbBuffAddType
{
    BUFF_REPLACE_EXISTING = 0,
    BUFF_RENEW_EXISTING = 1,
    BUFF_STACKS_AND_RENEWS = 2,
    BUFF_STACKS_AND_CONTINUE = 3,
    BUFF_STACKS_AND_OVERLAPS = 4,
}

/// <summary>Buff categories (BuffType, HasBuffOfType). BUFF_Silence, BUFF_Pacified and BUFF_Disarm share a value in retail.</summary>
public enum BbBuffType
{
    BUFF_Aura = 1,
    BUFF_CombatDehancer = 3,
    BUFF_CombatEnchancer = 2,
    BUFF_Damage = 0xB,
    BUFF_Fear = 0x14,
    BUFF_Frenzy = 0x13,
    BUFF_Haste = 0xD,
    BUFF_Heal = 0xC,
    BUFF_Internal = 0,
    BUFF_Invisibility = 5,
    BUFF_Invulnerability = 0x10,
    BUFF_Net = 0x15,
    BUFF_PhysicalImmunity = 0xF,
    BUFF_Polymorph = 8,
    BUFF_Silence = 6,
    BUFF_Sleep = 0x11,
    BUFF_NearSight = 0x12,
    BUFF_Slow = 9,
    BUFF_Snare = 0xA,
    BUFF_SpellImmunity = 0xE,
    BUFF_Stun = 4,
    BUFF_Taunt = 7,
    BUFF_Pacified = 6,
    BUFF_Disarm = 6,
    BUFF_Poison = 0x16,
    BUFF_Suppression = 0x17,
    BUFF_Blind = 0x18,
    BUFF_AmmoStack = 0x19,
}

/// <summary>Effect creation flags.</summary>
public enum BbEffectFlags
{
    EFFCREATE_KEEPALIVE = 1,
    EFFCREATE_UPDATE_ORIENTATION = 0x10,
    EFFCREATE_SIMULATE_WHILE_OFF_SCREEN = 0x20,
}

/// <summary>Unit order kinds for IssueOrder.</summary>
public enum BbAiOrder
{
    AI_ORDER_NONE = 0,
    AI_HOLD = 1,
    AI_MOVETO = 2,
    AI_ATTACKTO = 3,
    AI_TEMP_CASTSPELL = 4,
    AI_PETHARDATTACK = 5,
    AI_PETHARDMOVE = 6,
    AI_PETHARDRETURN = 9,
    AI_ATTACKMOVE = 7,
    AI_TAUNT = 8,
}

/// <summary>Damage source kinds (spell and non-spell).</summary>
public enum BbDamageSource
{
    NONSPELL_DAMAGESOURCE_RAW = 0,
    NONSPELL_DAMAGESOURCE_INTERNALRAW = 1,
    NONSPELL_DAMAGESOURCE_PERIODIC = 2,
    NONSPELL_DAMAGESOURCE_REACTIVE = 4,
    NONSPELL_DAMAGESOURCE_ONDEATH = 5,
    DAMAGESOURCE_RAW = 0,
    DAMAGESOURCE_INTERNALRAW = 1,
    DAMAGESOURCE_PERIODIC = 2,
    DAMAGESOURCE_PROC = 3,
    DAMAGESOURCE_TO_REACTIVE = 4,
    DAMAGESOURCE_TO_ONDEATH = 5,
    DAMAGESOURCE_SPELL = 6,
    DAMAGESOURCE_ATTACK = 7,
    DAMAGESOURCE_DEFAULT = 8,
    DAMAGESOURCE_SPELLAOE = 9,
    DAMAGESOURCE_SPELLPERSIST = 0xA,
    DAMAGESOURCE_REACTIVE = 4,
    DAMAGESOURCE_ONDEATH = 5,
}

/// <summary>Spawn anchors for BBSpawnMinion and teleport helpers.</summary>
public enum BbSpawnLocation
{
    SPAWN_HQ = 0,
    SPAWN_BARRACKS_LANE_R = 2,
    SPAWN_LOCATION = 1,
}

/// <summary>Channel stop conditions (both spellings registered by the exe).</summary>
public enum BbChannelingStopCondition
{
    CHANNELINGSTOPCONDITION_SUCCESS = 1,
    CHANNELINGSTOPCONDITION_CANCEL = 2,
    ChannelingStopCondition_NotCancelled = 0,
    ChannelingStopCondition_Success = 1,
    ChannelingStopCondition_Cancel = 2,
}

/// <summary>Channel stop sources (both spellings registered by the exe).</summary>
public enum BbChannelingStopSource
{
    CHANNELINGSTOPSOURCE_NORMAL = 1,
    CHANNELINGSTOPSOURCE_ANIMATION = 2,
    CHANNELINGSTOPSOURCE_LOSTTARGET = 3,
    CHANNELINGSTOPSOURCE_STUNNEDORSILENCEDORTAUNTED = 4,
    CHANNELINGSTOPSOURCE_CHANNELINGCONDITION = 5,
    CHANNELINGSTOPSOURCE_DIE = 6,
    CHANNELINGSTOPSOURCE_HEROREINCARNATE = 7,
    CHANNELINGSTOPSOURCE_MOVE = 8,
    CHANNELINGSTOPSOURCE_ATTACK = 9,
    ChannelingStopSource_NotCancelled = 0,
    ChannelingStopSource_TimeCompleted = 1,
    ChannelingStopSource_Animation = 2,
    ChannelingStopSource_LostTarget = 3,
    ChannelingStopSource_StunnedOrSilencedOrTaunted = 4,
    ChannelingStopSource_ChannelingCondition = 5,
    ChannelingStopSource_Die = 6,
    ChannelingStopSource_HeroReincarnate = 7,
    ChannelingStopSource_Move = 8,
    ChannelingStopSource_Attack = 9,
    ChannelingStopSource_Casting = 0xA,
    ChannelingStopSource_Unknown = 0xB,
}

/// <summary>Damage types.</summary>
public enum BbDamageType
{
    PHYSICAL_DAMAGE = 0,
    MAGIC_DAMAGE = 1,
    TRUE_DAMAGE = 2,
}

/// <summary>Basic-attack hit results.</summary>
public enum BbHitResult
{
    HIT_Normal = 0,
    HIT_Critical = 1,
    HIT_Miss = 3,
    HIT_Dodge = 2,
}

/// <summary>Spell targeting types.</summary>
public enum BbTargetType : uint
{
    TTYPE_Invalid = 0xFFFFFFFF,
    TTYPE_Self = 0x0,
    TTYPE_Target = 0x1,
    TTYPE_Area = 0x2,
    TTYPE_Cone = 0x3,
    TTYPE_SelfAOE = 0x4,
    TTYPE_TargetOrLocation = 0x5,
    TTYPE_Location = 0x6,
    TTYPE_Direction = 0x7,
}

/// <summary>Team ids.</summary>
public enum BbTeam
{
    TEAM_UNKNOWN = 0,
    TEAM_ORDER = 0x64,
    TEAM_CHAOS = 0xC8,
    TEAM_NEUTRAL = 0x12C,
    TEAM_OWNER = 0x191,
    TEAM_CASTER = 0x192,
    TEAM_TARGET = 0x193,
}

/// <summary>Spell slot groups.</summary>
public enum BbSlotType
{
    SpellSlots = 0,
    InventorySlots = 1,
    ExtraSlots = 2,
}

/// <summary>Spellbook selectors.</summary>
public enum BbSpellbook
{
    SPELLBOOK_CHAMPION = 0,
    SPELLBOOK_SUMMONER = 1,
}

/// <summary>Collision and range query modes.</summary>
public enum BbQueryMode
{
    FURTHEST_WITHIN_RANGE = 0,
    FIRST_COLLISION_HIT = 1,
    GET_NEAREST_IN_RANGE = 2,
    GET_NEAREST_IN_RANGE_INCLUDE_UNITS = 3,
    FIRST_WALL_HIT = 4,
}

/// <summary>What an issued order does to the current one.</summary>
public enum BbOrderMode
{
    CANCEL_ORDER = 0,
    POSTPONE_CURRENT_ORDER = 1,
}

/// <summary>Facing behaviour after a move.</summary>
public enum BbFacing
{
    FACE_MOVEMENT_DIRECTION = 0,
    KEEP_CURRENT_FACING = 1,
}

/// <summary>Resource (PAR) types.</summary>
public enum BbParType
{
    PAR_MANA = 0,
    PAR_ENERGY = 1,
    PAR_SOULS = 2,
    PAR_SHIELD = 3,
    PAR_OTHER = 4,
}

/// <summary>Engine limits and unit attribute flags.</summary>
public enum BbEngineLimit
{
    MAX_SPELLS_PER_CHAR = 4,
    NUM_INVENTORY_SLOTS = 6,
    MAX_SLOTS_PER_CHARACTER = 0xA,
    HAS_SUNGLASSES = 0,
}

/// <summary>
/// The comparison functions BuildingBlocksBase.luaobj defines for <c>CompareOp</c> (protos 8 to 30). They are Lua
/// functions in retail, not registered constants, so they have no numeric value.
/// </summary>
public enum BbComparison
{
    CO_EQUAL,
    CO_NOT_EQUAL,
    CO_LESS_THAN,
    CO_GREATER_THAN,
    CO_LESS_THAN_OR_EQUAL,
    CO_GREATER_THAN_OR_EQUAL,
    CO_SAME_TEAM,
    CO_DIFFERENT_TEAM,
    CO_DAMAGE_SOURCETYPE_IS,
    CO_DAMAGE_SOURCETYPE_IS_NOT,
    CO_IS_TYPE_AI,
    CO_IS_NOT_AI,
    CO_IS_TYPE_HERO,
    CO_IS_NOT_HERO,
    CO_IS_MELEE,
    CO_IS_RANGED,
    CO_RANDOM_CHANCE_LESS_THAN,
    CO_IS_TYPE_TURRET,
    CO_IS_NOT_TURRET,
    CO_IS_DEAD,
    CO_IS_NOT_DEAD,
    CO_IS_TARGET_IN_FRONT_OF_ME,
    CO_IS_TARGET_BEHIND_ME,
}

/// <summary>The arithmetic functions BuildingBlocksBase.luaobj defines for <c>MathOp</c> (protos 41 to 53).</summary>
public enum BbMathOp
{
    MO_MULTIPLY,
    MO_ADD,
    MO_SUBTRACT,
    MO_DIVIDE,
    MO_MIN,
    MO_MAX,
    MO_MODULO,
    MO_ROUND,
    MO_ROUNDUP,
    MO_ROUNDDOWN,
    MO_SIN,
    MO_COSINE,
    MO_TANGENT,
}

/// <summary>Name lookup for the symbolic enums, without reflection over the model.</summary>
internal static class BbNames<T> where T : struct, Enum
{
    private static readonly Dictionary<string, T> Map = Enum.GetNames<T>().ToDictionary(n => n, n => Enum.Parse<T>(n), StringComparer.Ordinal);

    public static bool TryParse(string name, out T value) => Map.TryGetValue(name, out value);

    public static bool Contains(string name) => Map.ContainsKey(name);
}

/// <summary>Name to value lookup over every constant enum.</summary>
public static class BbConstants
{
    private static readonly Dictionary<string, double> Values = Build();

    /// <summary>Globals that scripts reference but the stock client never defines, so Lua reads them as nil.</summary>
    public static readonly IReadOnlyList<string> UndefinedInRetail = ["True", "False", "NoValidTarget", "NoTargetYet"];

    public static IReadOnlyDictionary<string, double> All => Values;

    public static bool TryGetValue(string name, out double value) => Values.TryGetValue(name, out value);

    private static Dictionary<string, double> Build()
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        Add<BbSpellFlags>(values);
        Add<BbBuffAddType>(values);
        Add<BbBuffType>(values);
        Add<BbEffectFlags>(values);
        Add<BbAiOrder>(values);
        Add<BbDamageSource>(values);
        Add<BbSpawnLocation>(values);
        Add<BbChannelingStopCondition>(values);
        Add<BbChannelingStopSource>(values);
        Add<BbDamageType>(values);
        Add<BbHitResult>(values);
        Add<BbTargetType>(values);
        Add<BbTeam>(values);
        Add<BbSlotType>(values);
        Add<BbSpellbook>(values);
        Add<BbQueryMode>(values);
        Add<BbOrderMode>(values);
        Add<BbFacing>(values);
        Add<BbParType>(values);
        Add<BbEngineLimit>(values);
        return values;
    }

    private static void Add<T>(Dictionary<string, double> values) where T : struct, Enum
    {
        foreach (var name in Enum.GetNames<T>()) values.Add(name, Convert.ToDouble(Enum.Parse<T>(name)));
    }
}
