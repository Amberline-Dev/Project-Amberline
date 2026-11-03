using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Amberline.Data;

public sealed record ChampionData(
    string Name,
    double BaseHp,
    double HpPerLevel,
    double BaseMp,
    double MpPerLevel,
    double BaseDamage,
    double DamagePerLevel,
    double Armor,
    double ArmorPerLevel,
    double SpellBlock,
    double SpellBlockPerLevel,
    double HpRegen,
    double HpRegenPerLevel,
    double MpRegen,
    double MpRegenPerLevel,
    double AttackSpeedPerLevel,
    double MoveSpeed,
    double AttackRange,
    double PathfindingRadius,
    double GameplayRadius,
    IReadOnlyList<string> SpellNames,
    string ResourceType,
    InibinData Raw)
{
    /// <summary>The kind of the character's one resource pool (<c>Data*PARType</c>, see <see cref="PrimaryResourceType"/>).</summary>
    public PrimaryResourceType PrimaryResource => PrimaryResourceTypes.Parse(ResourceType);

    /// <summary>
    /// Whether the record names <c>Data*PARType</c> at all. An absent key is the loader's Mana default for a hero, but for a
    /// non-hero unit it means the character has no resource pool: only 18 of the 481 shipped records name one (Energy 4,
    /// Other 3, Shield 11), and the six non-hero ones (MaokaiSproutling, OriannaBall, SightWard, TeemoMushroom, VisionWard,
    /// WriggleLantern) are all Shield.
    /// </summary>
    public bool NamesParType { get; init; }

    // Raw GameplayRadius stays zero when absent; only combat applies the stock default: the client's character loader reads
    // GameplayCollisionRadius with GeneralCharacterData.inibin [GeneralDataHero] DefaultChampionCollisionRadius (65.0 shipped) as the default.
    public double CombatRadius => Raw.TryGetValue(InibinReader.KeyHash("Data*GameplayCollisionRadius"), out var value)
        ? value.AsClientFloat()
        : DefaultCollisionRadius;
    /// <summary>GeneralCharacterData [GeneralDataHero] DefaultChampionCollisionRadius, the default of GameplayCollisionRadius.</summary>
    public double DefaultCollisionRadius { get; init; } = 65.0;
    public IReadOnlyList<float> BasicAttackAnimationProbabilities { get; init; } = [];
    /// <summary>Data*SelectionRadius (client mOverrideCollisionRadius); -1 when absent.</summary>
    public double SelectionRadius { get; init; } = -1.0;
    // The client's character loader: an absent AcquisitionRange defaults to 750.
    public double AcquisitionRange { get; init; } = 750.0;
    // The client's character loader: the character's [Data]
    // PerceptionBubbleRadius, defaulting to GamePermanent.cfg [Vision] PerceptionBubbleRadius (1350).
    public double PerceptionBubbleRadius { get; init; } = 1350.0;

    /// <summary>
    /// The character's <c>[Data] ServerOnly</c>, whose default is its <c>NeverRender</c>: the client's character loader
    /// reads NeverRender into bit 0x10 of its flag byte, then ServerOnly through the client's ini getter
    /// with the NeverRender value as the default an absent key takes (bit 0x20). Six shipped characters are ServerOnly that way
    /// (NeverRender 1, no ServerOnly key: OdinMinionSpawnPortal, OdinQuestBuff, OdinSpeedShrine, SpellBook1, TestCube,
    /// XerathArcaneBarrageLauncher); OdinTestCubeRender, TestCubeRender and TestCubeRender10Vision set ServerOnly 0 against their
    /// NeverRender. The client gives a ServerOnly minion no replication manager when it is created.
    /// </summary>
    public bool ServerOnly { get; init; }
    // CharacterData death rewards. Missing keys take the native loader
    // defaults: Exp 48, Gold 25,
    // global gold/XP 0.
    public double GoldGivenOnDeath { get; init; } = 25.0;
    public double ExpGivenOnDeath { get; init; } = 48.0;
    public double GlobalGoldGivenOnDeath { get; init; }
    public double GlobalExpGivenOnDeath { get; init; }

    /// <summary>
    /// The skin's <c>AttributeFlags</c> (<c>mExtraAttributeFlags</c>, read by the client's skin-info loader): the
    /// number under <c>MeshSkin*AttributeFlags</c> for the base skin and <c>MeshSkin{n}*AttributeFlags</c> for skin <c>n</c>, 0 when absent. The one
    /// shipped flag is <c>HAS_SUNGLASSES</c> (bit 0): Vayne's base skin, Xin Zhao's skin 1, Twitch's skin 5 and a few more carry it.
    /// </summary>
    public int ExtraAttributeFlags(int skinId) =>
        Raw.TryGetValue(InibinReader.KeyHash(skinId > 0 ? $"MeshSkin{skinId}*AttributeFlags" : "MeshSkin*AttributeFlags"), out var value)
            ? value.AsClientInt() : 0;
}

public sealed record SpellData(
    string Name,
    IReadOnlyList<double> Cooldowns,
    IReadOnlyList<double> ManaCosts,
    IReadOnlyDictionary<int, IReadOnlyList<InibinValue>> Effects,
    double CastRange,
    double CastRadius,
    double MissileSpeed,
    double Coefficient,
    double Coefficient2,
    int MaxRank,
    double CastTime,
    double TotalTime,
    InibinData Raw)
{
    // Instacast runs the script immediately. A script may start a separate
    // non-instant spell (for example RocketGrabMissile); that spell owns its delay.
    public bool IsInstant => InstantFlag(Raw);

    internal static bool InstantFlag(InibinData raw) => raw.TryGetValue(InibinReader.KeyHash("SpellData*Flags"), out var flags) &&
        flags.IsNumeric && ((long)flags.AsDouble() & 4) != 0;
    public double CastDelay => IsInstant ? 0.0 : CastTime;

    /// <summary>
    /// A spell inibin entry as the client's generic getter reads a flag: the 0/1 values live in the boolean section, some in the
    /// string section ("0", "1"), the rest are numbers; an absent key is false.
    /// </summary>
    public bool RawFlag(string key) => Raw.TryGetValue(InibinReader.KeyHash(key), out var value) &&
        (value.Kind == InibinValueKind.Boolean ? value.Boolean
            : value.Kind == InibinValueKind.String ? double.TryParse(value.AsString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed != 0.0
            : value.IsNumeric && value.AsDouble() != 0.0);

    /// <summary>A spell inibin string entry, empty when absent or not a string.</summary>
    public string RawText(string key) => Raw.TryGetValue(InibinReader.KeyHash(key), out var value) && value.Kind == InibinValueKind.String
        ? value.AsString() : "";

    /// <summary>
    /// The particle the stock server's default hit pass draws on every unit a spell hits:
    /// <c>HitEffectName</c> when <c>HaveHitEffect</c> is set, attached to <c>HitBoneName</c> when <c>HaveHitBone</c> is.
    /// </summary>
    public (string Effect, string? Bone)? DefaultHitEffect =>
        RawFlag("SpellData*HaveHitEffect") && RawText("SpellData*HitEffectName") is { Length: > 0 } effect
            ? (effect, RawFlag("SpellData*HaveHitBone") && RawText("SpellData*HitBoneName") is { Length: > 0 } bone ? bone : null)
            : null;

    /// <summary>The particle of the default after pass (<c>DrawDefaultHitEffects_After</c>): <c>AfterEffectName</c> when <c>HaveAfterEffect</c> is set.</summary>
    public string? DefaultAfterEffect =>
        RawFlag("SpellData*HaveAfterEffect") && RawText("SpellData*AfterEffectName") is { Length: > 0 } effect ? effect : null;
    public bool CantCancelWhileWindingUp => CantCancelWhileWindingUpFlag(Raw);

    internal static bool CantCancelWhileWindingUpFlag(InibinData raw) =>
        raw.TryGetValue(InibinReader.KeyHash("SpellData*CantCancelWhileWindingUp"), out var value) &&
        (value.Kind == InibinValueKind.Boolean ? value.Boolean : value.IsNumeric && value.AsDouble() != 0);

    /// <summary>
    /// <c>SPELL_PersistThroughDeath</c> (bit 8 of <c>SpellData*Flags</c>). The client's persistence predicate reads it beside the script's
    /// <c>PersistsThroughDeath</c> override.
    /// </summary>
    internal static bool PersistThroughDeathFlag(InibinData raw) =>
        raw.TryGetValue(InibinReader.KeyHash("SpellData*Flags"), out var flags) && flags.IsNumeric && ((long)flags.AsDouble() & 8) != 0;

    /// <summary>
    /// <c>SpellData*CantCancelWhileChanneling</c> (the spell data's flag): the cancel-blocked check refuses a plain Stop while the unit
    /// channels it, unless the unit is suppressed or cannot cast. 86 of the 1117 shipped spells set it (Crowstorm, Infinite Duress, Stand United ...).
    /// </summary>
    public bool CantCancelWhileChanneling => CantCancelWhileChannelingFlag(Raw);

    internal static bool CantCancelWhileChannelingFlag(InibinData raw) =>
        raw.TryGetValue(InibinReader.KeyHash("SpellData*CantCancelWhileChanneling"), out var value) &&
        (value.Kind == InibinValueKind.Boolean ? value.Boolean : value.IsNumeric && value.AsDouble() != 0);

    // mSpellRevealsChampion: the stock SpellData loader reads the key
    // through the generic inibin reader with default 1, so an absent key reveals (most spells ship
    // none) and only an explicit False/0 (OrianaIzunaCommand, JavelinToss, ...) does not.
    public bool RevealsChampion => RevealsChampionFlag(Raw);

    internal static bool RevealsChampionFlag(InibinData raw) =>
        !raw.TryGetValue(InibinReader.KeyHash("SpellData*SpellRevealsChampion"), out var value) ||
        (value.Kind == InibinValueKind.Boolean ? value.Boolean : value.IsNumeric && value.AsDouble() != 0);

    // IsToggleSpell: the stock SpellData loader reads it into its field (default false). The client keeps a
    // toggle state on the slot of such a spell (the slot's state: 1 when the SpellToggleSlot buff is added, 2 when it is removed,
    // the toggle handlers), shows "Click to turn on/off" in its tooltip (the state-2 string) and treats a cast of a
    // state-2 slot as a turn-on (the spellbook's cast handler). 46 of the 66 spells that have a SpellToggleSlot buff set it, so
    // it marks a slot with an on/off display, not a spell that is free to turn off.
    public bool IsToggleSpell => Raw.TryGetValue(InibinReader.KeyHash("SpellData*IsToggleSpell"), out var toggle) &&
        (toggle.Kind == InibinValueKind.Boolean ? toggle.Boolean : toggle.IsNumeric && toggle.AsDouble() != 0);

    /// <summary>
    /// <c>SpellData*CastRangeUseBoundingBoxes</c> (default false): the unit targeters' range test is edge to
    /// edge for such a spell (see <c>BbChampionModule.UnitCastReach</c>). Six shipped spells set it: Consume, Feast, Fling, MonkeyKingConsume,
    /// RivenFeint and RivenIzunaBlade.
    /// </summary>
    /// <summary>
    /// <c>SpellData*MissileFixedTravelTime</c> (default 0): a missile with a positive value arrives that
    /// long after it leaves, whatever the distance. The client's homing missile flies the distance left over the time left, and
    /// the launch gives a point or line missile the speed distance / time. Shipped: MaokaiSapling2Boom 0.85, OrianaDissonance
    /// 0.75, RivenLightsaberMissile and RivenLightsaberMissileSide 1.0.
    /// </summary>
    public double MissileFixedTravelTime => Raw.TryGetValue(InibinReader.KeyHash("SpellData*MissileFixedTravelTime"), out var value) &&
        value.Kind != InibinValueKind.Boolean && value.IsNumeric ? value.AsDouble() : 0.0;

    public bool CastRangeUseBoundingBoxes => Raw.TryGetValue(InibinReader.KeyHash("SpellData*CastRangeUseBoundingBoxes"), out var value) &&
        (value.Kind == InibinValueKind.Boolean ? value.Boolean : value.IsNumeric && value.AsDouble() != 0);

    // TTYPE_Target is 1 in the shipped client. Inibin packs some 0/1
    // integer fields in its boolean section, including these targeting values. the spell data loader reads TargettingType through
    // the int getter with default 1, so a spell without the key is a unit-target spell.
    public bool TargetsUnit => !Raw.TryGetValue(InibinReader.KeyHash("SpellData*TargettingType"), out var value) ||
        (value.Kind is InibinValueKind.ByteVector or InibinValueKind.FloatVector ? false : value.AsClientInt() == 1);

    /// <summary>
    /// The cast range at a spell level, the one place every range read goes through. The stock client keeps seven entries:
    /// The spell data loader reads <c>CastRange</c> and then <c>CastRange1</c>..<c>CastRange6</c>,
    /// each defaulting to the base value, and the slot's cast-range getter (a non-zero <c>BBOverrideCastRange</c>
    /// value first, else the entry for its level) indexes them by the spell's level, so level 0 reads the base entry
    /// and level N reads <c>CastRangeN</c>. The flying line missile asks the same table through the child spell's cast level
    ///. <see cref="CastRange"/> is only the base entry: Yorick's W reaches 600 and E 550 at
    /// every rank although their base entries say 550 and 650. A level outside 1..6 (an unlearned spell) and an absent or
    /// non-numeric entry fall back to the base entry.
    /// </summary>
    // The range normaliser, run for every spell the spell manager adds, lifts each of the seven entries at or
    // below 1.0 to 1.0, so a shipped range of 0 or 1 reads as 1 in the base entry and in CastRangeAt. A non-self unit spell with such a range
    // now gets the 1-unit distance test of BbChampionModule (its range > 0 gate); self, direction and line types keep their explicit 0, and a
    // cone without SpellData*CastConeDistance takes the lifted base entry as its radius. An ABSENT base entry reads 400 as in the client.
    public double CastRangeAt(int rank) =>
        rank is >= 1 and <= MaxSpellLevel && Raw.TryGetValue(InibinReader.KeyHash($"SpellData*CastRange{rank}"), out var value) && value.IsNumeric
            ? Math.Max(1.0, value.AsDouble()) : CastRange;

    /// <summary>
    /// The length of a line missile cast at <paramref name="rank"/>. The client reads its level table at the wire level, <c>spell_level</c> = rank
    /// minus one (<c>SpellPackets.BuildCastInfo</c>). The table is the base CastRange followed by CastRange1..6 (the spell data's level table)
    /// so entry 0 is the base. Reconstruction (batch 433): flights read the table at the wire level,
    /// so Talon's Shadow Assault blades (forced level 1) fly the base 575, not CastRange1 (1200), as the owner saw natively.
    /// </summary>
    public double LineRangeAt(int rank) => CastRangeAt(rank - 1);

    /// <summary>The maximum spell level (MAX_SPELL_LEVEL): the client's spell data setter asserts level &lt;= 6.</summary>
    public const int MaxSpellLevel = 6;

    public double EffectNumber(int effect, int rank1Based)
    {
        if (rank1Based < 1) throw new ArgumentOutOfRangeException(nameof(rank1Based));
        if (!Effects.TryGetValue(effect, out var ranks) || rank1Based > ranks.Count)
            throw new InvalidDataException($"Spell {Name} has no effect {effect} rank {rank1Based}");
        var value = ranks[rank1Based - 1];
        // The client reads an effect amount through the float getter, which takes the numeric prefix of a text value
        // (atof): GatlingGun and XenZhaoSweep ship "4.5.0000" / "1.5.0000" and read 4.5 / 1.5. Text with no number in front
        // (a script name) is not an amount; refusing it keeps a mis-indexed read from becoming a silent 0.
        if (value.Kind == InibinValueKind.String && InibinValue.TryAtofPrefix(value.AsString(), out var prefix))
            return prefix;
        if (!value.IsNumeric)
            throw new InvalidDataException($"Spell {Name} effect {effect} rank {rank1Based} is {value.Kind} ({value})");
        return value.AsDouble();
    }
}

public sealed record ChampionLevelStats(
    double MaxHp,
    double MaxMp,
    double AttackDamage,
    double Armor,
    double SpellBlock,
    double HpRegen,
    double MpRegen,
    double AttackSpeedMod,
    double MoveSpeed,
    double AttackRange);

/// <summary>Typed facade over the shipped champion, spell, and collision data.</summary>
public sealed partial class GameData
{
    private const int MaxRanks = 6;
    // PublicationOnly here and in ConfigureDefault: a failed load is not kept for the process's life (batch 657).
    private static Lazy<GameData> DefaultInstance = new(() => new GameData(), LazyThreadSafetyMode.PublicationOnly);
    private readonly Dictionary<string, ChampionData> _champions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Name, int? RankCount), SpellData> _spells = new();
    private InibinData? _globalSpellData;
    private IReadOnlyList<float>? _criticalKarmaConstants;
    private ClientTextConfig? _nseai;
    private HashSet<string>? _clientBuffNames;
    private HashSet<string>? _buffDataNames;
    private string[]? _spellInibinNames;
    private readonly Dictionary<string, bool> _uncancellableWindups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _uncancellableChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _persistsThroughDeath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _channelDurations = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _clientParticleNames;

    public static GameData Default => DefaultInstance.Value;

    /// <summary>
    /// Makes <see cref="Default"/> read the map of <paramref name="paths"/> (the Harrowing rift's Constants.var, Items.inibin and scripts). A
    /// server or headless process plays one match, so it calls this once, before any match code, and only when its map is not Map1.
    /// </summary>
    public static void ConfigureDefault(GameDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        DefaultInstance = new Lazy<GameData>(() => new GameData(paths), LazyThreadSafetyMode.PublicationOnly);
    }
    public GameDataPaths Paths { get; }

    private MapConstants? _levelConstants;

    /// <summary>Summoner's Rift LEVELS/Map1/Constants.var, loaded once.</summary>
    /// <summary>The Constants.var of the map these paths name (LEVELS/&lt;MapName&gt;): Map1 unless the game data was made for the Harrowing rift.</summary>
    public MapConstants LevelConstants => _levelConstants ??= MapConstants.Load(Paths, Paths.MapName);

    private int? _extraSummonerItemId;
    private string? _recallSpellName;

    /// <summary>
    /// Batch 460: the map's extra summoner item, the recall in inventory slot 6 (spell slot 10): <c>LEVELS/&lt;map&gt;/Mission.inibin</c>
    /// <c>ExtraSummonerItem*ItemID</c>, 2005 on The Crystal Scar. The client's HUD reads only the same section's Tooltip and Icon
    /// (<c>LEVELS\%s\Mission.ini</c>, ), so the item id is the server's to send. Reconstruction: 2001 (Recall) when
    /// the map ships no key, as Summoner's Rift and the Harrowing rift do (the item the server always sent there).
    /// </summary>
    public int ExtraSummonerItemId => _extraSummonerItemId ??= ReadExtraSummonerItemId();

    /// <summary>
    /// Batch 460: the recall spell, <c>Data*SpellName</c> of <see cref="ExtraSummonerItemId"/>: Recall (item 2001), OdinRecall on The Crystal
    /// Scar (item 2005; 4.5 s ChannelDuration in OdinRecall.luaobj against Recall's 8). The client loads an item by its file name
    /// (the item array's add, which reads the name as a number), so 2005.inibin's own Data*ItemID 2002 is not read.
    /// </summary>
    public string RecallSpellName => _recallSpellName ??=
        new ItemCatalog(Paths).Get(ExtraSummonerItemId)?.SpellName is { Length: > 0 } name ? name : "Recall";

    private int ReadExtraSummonerItemId()
    {
        var path = Paths.Under("LEVELS", Paths.MapName, "Mission.inibin");
        return File.Exists(path) && InibinReader.Load(path).TryGetValue(InibinReader.KeyHash("ExtraSummonerItem*ItemID"), out var id)
            ? (int)id.AsClientFloat()
            : 2001;
    }

    public GameData(GameDataPaths? paths = null) => Paths = paths ?? GameDataPaths.Default;

    /// <summary>The spell inibin names under <c>Data/Spells</c> (the files <see cref="Spell"/> loads), for resolving the name hash a cast packet carries.</summary>
    public IReadOnlyList<string> SpellInibinNames => _spellInibinNames ??= Directory.Exists(Paths.Data("Spells"))
        ? CaseInsensitivePaths.EnumerateFiles(Paths.Data("Spells"), "*.inibin").Select(path => Path.GetFileNameWithoutExtension(path)).ToArray()
        : [];

    /// <summary>
    /// Whether a cast of this spell gives the client a windup its plain Stop cannot cancel: <c>SpellData*CantCancelWhileWindingUp</c>
    /// on a spell that is not an instacast. An instacast (<c>SpellData*Flags</c> bit 4)
    /// runs its effects at once in the spellbook's instacast handler and never creates a SpellCaster, whatever cast
    /// time its packet carries. Read from the raw inibin and cached, so a name that is not a spell file is false and a spell a cast
    /// never names is never loaded.
    /// </summary>
    public bool SpellHasUncancellableWindup(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_uncancellableWindups.TryGetValue(name, out var cached)) return cached;
        var path = Paths.Data($"Spells/{name}.inibin");
        var flag = false;
        if (File.Exists(path))
        {
            var raw = InibinReader.Load(path);
            flag = SpellData.CantCancelWhileWindingUpFlag(raw) && !SpellData.InstantFlag(raw);
        }
        _uncancellableWindups.Add(name, flag);
        return flag;
    }

    /// <summary>
    /// Whether the spell's data carries <c>SPELL_PersistThroughDeath</c> (<see cref="SpellData.PersistThroughDeathFlag"/>). A buff that
    /// survives a death is one whose script says <c>PersistsThroughDeath</c> or whose spell data has this bit: the client's one persistence
    /// predicate is that OR. Cached per name like the other spell flags.
    /// </summary>
    public bool SpellPersistsThroughDeath(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_persistsThroughDeath.TryGetValue(name, out var cached)) return cached;
        var path = Paths.Data($"Spells/{name}.inibin");
        var flag = File.Exists(path) && SpellData.PersistThroughDeathFlag(InibinReader.Load(path));
        _persistsThroughDeath.Add(name, flag);
        return flag;
    }

    /// <summary>
    /// Whether the client refuses a plain Stop while it runs a channel of this spell: <c>SpellData*CantCancelWhileChanneling</c> on a spell that is not
    /// an instacast (an instacast never creates a SpellCaster, so it has no channel to refuse a stop for). Cached like
    /// <see cref="SpellHasUncancellableWindup"/>.
    /// </summary>
    public bool SpellHasUncancellableChannel(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_uncancellableChannels.TryGetValue(name, out var cached)) return cached;
        var path = Paths.Data($"Spells/{name}.inibin");
        var flag = false;
        if (File.Exists(path))
        {
            var raw = InibinReader.Load(path);
            flag = SpellData.CantCancelWhileChannelingFlag(raw) && !SpellData.InstantFlag(raw);
        }
        _uncancellableChannels.Add(name, flag);
        return flag;
    }

    /// <summary>
    /// The length of the channel the client actually runs for this spell: the spell data's channel duration, which
    /// the spell's init copies from the shipped script's <c>ChannelDuration</c> global.
    /// It is not the cast packet's
    /// total time: the client's cast frame starts the channel only when this value is above zero;
    /// so a spell whose script lacks the global never channels however long its packet says it runs. 0 for a missing or
    /// unreadable script. Cached, like <see cref="SpellHasUncancellableChannel"/>.
    /// </summary>
    public double SpellChannelDuration(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_channelDurations.TryGetValue(name, out var cached)) return cached;
        var duration = 0.0;
        if (File.Exists(Paths.Data($"Spells/{name}.luaobj")))
        {
            var loaded = LuaObjectReader.LoadData(Paths, $"Spells/{name}.luaobj");
            if (loaded.Status == LuaLoadStatus.Success &&
                loaded.Globals.TryGetValue("ChannelDuration", out var value) && value is LuaValue.Number number && number.Value > 0.0)
                duration = number.Value;
        }
        _channelDurations.Add(name, duration);
        return duration;
    }

    private HashSet<string>? _spellNames;

    /// <summary>
    /// Whether the shipped spell data holds <paramref name="name"/> (<c>DATA/Spells/&lt;name&gt;.inibin</c>, matched ignoring case: the client's
    /// <c>SpellManager</c> hashes the lower-cased name). The stock client asserts on a slot-name packet whose spell it cannot find
    /// (<c>ChangeSlotSpellName</c>), so the script engine checks here before it sends one.
    /// </summary>
    public bool HasSpell(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _spellNames ??= CaseInsensitivePaths.EnumerateFiles(Paths.Data("Spells"), "*.inibin")
            .Select(path => Path.GetFileNameWithoutExtension(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _spellNames.Contains(name);
    }

    /// <summary>
    /// Whether <c>DATA/Characters/&lt;name&gt;/&lt;name&gt;.inibin</c> exists: the character record a pushed character-data layer names
    /// (the client's character array logs and creates nothing for a name it cannot load).
    /// </summary>
    public bool HasCharacterData(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.IndexOfAny(['/', (char)92, ':']) < 0 && File.Exists(Paths.Data($"Characters/{name}/{name}.inibin"));
    }

    /// <summary>
    /// The native BuffHashMap indexes spell/item scripts, not arbitrary server markers. A buff's binding name is its spell or item
    /// script's file name, so this gate stays on the binding name; the name sent is <c>ClientBuffName</c>.
    /// </summary>
    public bool HasClientBuff(string name)
    {
        _clientBuffNames ??= new[] { "Spells", "Items" }
            .SelectMany(directory => Directory.EnumerateFiles(Paths.Data(directory)))
            .Where(path => Path.GetExtension(path) is ".lua" or ".luaobj")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _clientBuffNames.Contains(name);
    }

    /// <summary>
    /// Whether the shipped <c>DATA/Buffs</c> folder has <c>&lt;name&gt;.inibin</c>: the icon and tooltip the stock client shows for a buff of
    /// that name. The client builds its BuffHashMap from these files (<c>Data\Buffs\*.ini</c>).
    /// </summary>
    public bool HasBuffData(string name)
    {
        _buffDataNames ??= Directory.Exists(Paths.Data("Buffs"))
            ? CaseInsensitivePaths.EnumerateFiles(Paths.Data("Buffs"), "*.inibin")
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return _buffDataNames.Contains(name);
    }

    /// <summary>
    /// The name a buff's add and remove packets carry: always the binding name, the spell or item script the server adds the buff from.
    /// The stock client builds a buff object only from a <c>Data\Spells\%s.lua</c> script of the wire name
    /// and the 0xBF handler skips an add whose name builds no object.
    /// The script's <c>BuffName</c> is not sent (Batch 357: binding stem): a name with no script (Sivir's Spell Shield)
    /// builds nothing, and one that names another script (Lee E2's RaiseMorale, Gangplank's) builds that script's object. Batch 181
    /// sent the BuffName when a Data\Buffs entry existed, which holds tooltip keys only.
    /// </summary>
    public string ClientBuffName(string bindName) => bindName;

    /// <summary>
    /// Whether the stock client can resolve a particle name sent in a create-effect packet. The client builds its
    /// <c>EffectHashMap</c> from the particle name list of <c>DATA/Particles</c> (the directory listing helper
    /// reads <c>troy.filelist</c>, a NUL separated list of <c>.troy</c> names, and hashes each name without its extension); a hash
    /// outside that map logs a "Match not found" message and creates nothing. The match is by stem, ignoring case.
    /// </summary>
    public bool HasClientParticle(string effectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(effectName);
        if (_clientParticleNames is null)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = Paths.Data("Particles/troy.filelist");
            if (File.Exists(list))
                foreach (var entry in System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(list)).Split('\0', StringSplitOptions.RemoveEmptyEntries))
                    names.Add(ParticleStem(entry));
            // The listing falls back to the directory itself when there is no list file.
            foreach (var path in CaseInsensitivePaths.EnumerateFiles(Paths.Data("Particles"), "*.troy"))
                names.Add(ParticleStem(Path.GetFileName(path)));
            _clientParticleNames = names;
        }
        return _clientParticleNames.Contains(ParticleStem(effectName));
    }

    /// <summary>
    /// The name the client hashes for a particle: the text before its last dot, with the white space around it removed first. Shipped scripts
    /// leave spaces after an effect's name ("exile_E_mis.troy  ", RivenFeint.luaobj; "renektoncleave_trail_rage.troy  ", RenektonCleave.luaobj) and
    /// one mistype the extension ("galio_windTunnel_speed_buf_team_red.tro", Galio's E). The client's map holds the stems of the list's names, so the
    /// wire carries this stem's hash, and <see cref="HasClientParticle"/> judges the particle by the same stem.
    /// </summary>
    public static string ParticleStem(string name)
    {
        var trimmed = name.Trim();
        var dot = trimmed.LastIndexOf('.');
        return dot > 0 ? trimmed[..dot] : trimmed;
    }

    public IReadOnlyList<float> CriticalKarmaConstants
    {
        get
        {
            if (_criticalKarmaConstants is not null) return _criticalKarmaConstants;
            var raw = InibinReader.Load(Paths.Data("Globals/Critical.inibin"));
            var values = new float[201];
            for (int i = 0; i < values.Length; i++)
            {
                if (!raw.TryGetValue(InibinReader.KeyHash($"Karma*Critical{i}"), out var value))
                    throw new InvalidDataException($"Missing Karma Critical{i}.");
                values[i] = (float)value.AsClientFloat();
                if (!float.IsFinite(values[i]) || values[i] < 0 || values[i] > 1)
                    throw new InvalidDataException($"Invalid Karma Critical{i}.");
            }
            return _criticalKarmaConstants = Array.AsReadOnly(values);
        }
    }

    private double? _defaultChampionCollisionRadius;

    /// <summary>
    /// DATA/Characters/GeneralCharacterData.inibin [GeneralDataHero] DefaultChampionCollisionRadius (65.0 shipped), the value the
    /// client's CharacterData loader gives GameplayCollisionRadius when a unit has none; 65.0 when the file lacks it.
    /// </summary>
    public double DefaultChampionCollisionRadius => _defaultChampionCollisionRadius ??= LoadDefaultChampionCollisionRadius();

    private double LoadDefaultChampionCollisionRadius()
    {
        var path = Paths.Data("Characters/GeneralCharacterData.inibin");
        if (File.Exists(path) &&
            InibinReader.Load(path).TryGetValue(InibinReader.KeyHash("GeneralDataHero*DefaultChampionCollisionRadius"), out var value))
            return value.AsClientFloat();
        return 65.0;
    }

    private double? _perceptionBubbleDefault;

    /// <summary>
    /// GamePermanent.cfg [Vision] PerceptionBubbleRadius, the CharacterData default for every unit that does
    /// not set its own (ShacoBox sets 700). The stock loader falls back to 1350 when the key is absent.
    /// </summary>
    public double PerceptionBubbleDefault => _perceptionBubbleDefault ??= LoadPerceptionBubbleDefault();

    private double LoadPerceptionBubbleDefault()
    {
        var path = Paths.Data("CFG/defaults/GamePermanent.cfg");
        return File.Exists(path) ? ClientTextConfig.Load(path).Float("Vision", "PerceptionBubbleRadius", 1350.0) : 1350.0;
    }

    public ChampionData Champion(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_champions.TryGetValue(name, out var cached)) return cached;
        var data = LoadChampionInibin(name);
        // The client's character loader reads every number through the generic float
        // getter and gives an ABSENT key the default written next to it: BaseHP/BaseMP 100, HP/MPPerLevel 10, BaseStaticHP/MPRegen 0.3,
        // BaseDamage/DamagePerLevel 10, Armor/ArmorPerLevel 1, AttackRange 100, MoveSpeed 100 (read through the INT getter),
        // GameplayCollisionRadius = GeneralCharacterData's DefaultChampionCollisionRadius; the others default to 0. The shipped
        // heroes, minions and monsters carry nearly every key, so the defaults reach the buildings (Order/ChaosNexus, inhibitors),
        // the super minions (no per-level keys), wards and a few effect units.
        double C(string key, double fallback = 0.0) => Number(data, key, fallback);
        var champion = new ChampionData(
            name,
            C("Data*BaseHP", 100.0), C("Data*HPPerLevel", 10.0),
            C("Data*BaseMP", 100.0), C("Data*MPPerLevel", 10.0),
            C("Data*BaseDamage", 10.0), C("Data*DamagePerLevel", 10.0),
            C("Data*Armor", 1.0), C("Data*ArmorPerLevel", 1.0),
            C("Data*SpellBlock"), C("Data*SpellBlockPerLevel"),
            C("Data*BaseStaticHPRegen", 0.3), C("Data*HPRegenPerLevel"),
            C("Data*BaseStaticMPRegen", 0.3), C("Data*MPRegenPerLevel"),
            C("Data*AttackSpeedPerLevel"), Math.Truncate(C("Data*MoveSpeed", 100.0)),
            C("Data*AttackRange", 100.0),
            // Reconstruction: a unit without PathfindingCollisionRadius reads -1 ("unset") in the client, and retail's server value is not recovered; every consumer here uses the
            // value as a radius, and only structures and effect units lack the key, so it stays 0.
            C("Data*PathfindingCollisionRadius"), C("Data*GameplayCollisionRadius"),
            Enumerable.Range(1, 4).Select(i => String(data, $"Data*Spell{i}", string.Empty)).ToArray(),
            String(data, "Data*PARType", "Mana"), data)
        {
            BasicAttackAnimationProbabilities = LoadAttackAnimationProbabilities(name, data),
            SelectionRadius = C("Data*SelectionRadius", -1.0),
            AcquisitionRange = C("Data*AcquisitionRange", 750.0),
            PerceptionBubbleRadius = C("Data*PerceptionBubbleRadius", PerceptionBubbleDefault),
            GoldGivenOnDeath = C("Data*GoldGivenOnDeath", 25.0),
            ExpGivenOnDeath = C("Data*ExpGivenOnDeath", 48.0),
            GlobalGoldGivenOnDeath = C("Data*GlobalGoldGivenOnDeath"),
            GlobalExpGivenOnDeath = C("Data*GlobalExpGivenOnDeath"),
            DefaultCollisionRadius = DefaultChampionCollisionRadius,
            NamesParType = data.TryGetValue(InibinReader.KeyHash("Data*PARType"), out _),
            ServerOnly = C("Data*ServerOnly", C("Data*NeverRender")) != 0.0,
        };
        _champions.Add(name, champion);
        return champion;
    }

    /// <summary>
    /// The character record of <paramref name="name"/>, or null when the shipped files have none. A unit the engine made up
    /// (a test body, a synthetic name) has no record and therefore no character-driven stats.
    /// </summary>
    public ChampionData? OptionalChampion(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_champions.TryGetValue(name, out var cached)) return cached;
        return File.Exists(Paths.Data($"Characters/{name}/{name}.inibin")) ? Champion(name) : null;
    }

    /// <summary>
    /// The spellbook's slot defaults, read from the stock executable's table:
    /// Q, W and E five ranks, R three.
    /// </summary>
    public static IReadOnlyList<int> NormalSpellMaxLevels { get; } = [5, 5, 5, 3];

    /// <summary>
    /// The highest rank a champion can learn in a slot (0 Q to 3 R), the retail rule: the character's <c>Data*MaxLevels</c> vector when it
    /// ships one with a non-zero entry (the loader reads four floats and rounds them; Karma 6/6/6/1, Udyr and his three
    /// stance records 5/5/5/5), otherwise <see cref="NormalSpellMaxLevels"/>. It is a property of the spellbook slot, not of the spell
    /// (<see cref="SpellData.MaxRank"/> only counts numeric <c>ManaCost</c> keys).
    /// </summary>
    public int SpellMaxLevel(string championName, int slot)
    {
        if ((uint)slot >= (uint)NormalSpellMaxLevels.Count) throw new ArgumentOutOfRangeException(nameof(slot));
        if (Champion(championName).Raw.TryGetValue(InibinReader.KeyHash("Data*MaxLevels"), out var value) && value.Vector is not null)
        {
            var levels = value.AsClientVector();
            if (levels.Count > slot && levels.Any(level => level != 0.0))
                return (int)Math.Round(levels[slot], MidpointRounding.AwayFromZero);
        }
        return NormalSpellMaxLevels[slot];
    }

    /// <summary>
    /// The default champion levels the spellbook asks for each rank (0-based rank) of each slot: Q, W and E at 1 3 5 7 9, R at 6 11 16, the rest 99
    /// (never). The six-wide rows of the stock executable's table (indexed slot * 6 + rank).
    /// </summary>
    private static readonly int[][] NormalSpellRankGates = [[1, 3, 5, 7, 9, 99], [1, 3, 5, 7, 9, 99], [1, 3, 5, 7, 9, 99], [6, 11, 16, 99, 99, 99]];

    /// <summary>
    /// The champion level that <paramref name="rank"/> (1-based) of <paramref name="slot"/> (0 Q to 3 R) needs, the retail spellbook rule
    /// (the getter the spellbook asks when it decides whether a slot can be levelled): the character's own row for the slot
    /// when it ships one, else the table. A rank past the row reads the table (99, never).
    /// Reconstruction: the getter's read of the character's per-slot int vectors (a vector of vectors, element 16 bytes) was traced;
    /// the loader that fills them was not. The shipped keys are <c>Data*SpellsUpLevels1</c> to <c>4</c> (the key hashes of the four
    /// <c>fea777fc</c> to <c>fea777ff</c> entries of the character inibins match those names), each a string of six space-separated levels:
    /// Udyr "1 3 5 7 9 99" in all four slots (his R is a normal rank-five spell), Karma Q, W and E "1 3 5 7 9 11" and R "1 95 96 97 98 99".
    /// </summary>
    public int SpellRankGate(string championName, int slot, int rank)
    {
        if ((uint)slot >= (uint)NormalSpellRankGates.Length) throw new ArgumentOutOfRangeException(nameof(slot));
        if (rank < 1) throw new ArgumentOutOfRangeException(nameof(rank));
        if (Champion(championName).Raw.TryGetValue(InibinReader.KeyHash($"Data*SpellsUpLevels{slot + 1}"), out var value) &&
            value.Kind == InibinValueKind.String)
        {
            var levels = value.AsString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (rank <= levels.Length && int.TryParse(levels[rank - 1], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var level))
                return level;
        }
        var row = NormalSpellRankGates[slot];
        return rank <= row.Length ? row[rank - 1] : 99;
    }

    public SpellData Spell(string name, int? rankCount = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (rankCount is < 1 or > MaxRanks)
            throw new ArgumentOutOfRangeException(nameof(rankCount), "rank_count must be an integer from 1 to 6");
        var cacheKey = (name, rankCount);
        if (_spells.TryGetValue(cacheKey, out var cached)) return cached;

        var data = LoadSpellInibin(name);
        // Reconstruction: MaxRank is the run of leading numeric ManaCost{N} keys. The client's own rank limit is the spellbook slot
        // (5, 5, 5, 3, or a character's Data*MaxLevels), not a spell field; SpellMaxLevel(champion, slot) is
        // that rule and every level-up limit uses it. The compiler pads the unused
        // ranks of a spell (Disintegrate's sixth, an ultimate's fourth and fifth) with a boolean-segment zero, so only numeric keys count.
        var storedMana = PerRank(data, "SpellData*ManaCost{0}", countBooleans: false);
        var maxRank = rankCount ?? storedMana.Count;
        IReadOnlyList<double> mana = rankCount is null
            ? storedMana
            : Enumerable.Range(1, maxRank).Select(rank => Number(data, $"SpellData*ManaCost{rank}", Number(data, "SpellData*ManaCost"))).ToArray();

        // The spell data loader: Cooldown defaults to 10.0 and CooldownN to that base value, rank by
        // rank. A spell with three Cooldown{N} keys and a base value keeps its three and takes the base for the rest; the old
        // all-or-nothing flat fallback gave Kayle's Intervention, Shaco's Hallucinate and Veigar's ultimate their base cooldown at every rank
        // whenever they were asked for more ranks than they ship cooldowns for.
        var baseCooldown = Number(data, "SpellData*Cooldown", 10.0);
        var cooldowns = Enumerable.Range(1, maxRank)
            .Select(rank => Number(data, $"SpellData*Cooldown{rank}", baseCooldown)).ToList();

        var effects = new Dictionary<int, IReadOnlyList<InibinValue>>();
        for (var effect = 1; effect <= 5; effect++)
        {
            var values = Enumerable.Range(1, maxRank)
                .Select(rank => OptionalValue(data, $"SpellData*Effect{effect}Level{rank}Amount"))
                .ToArray();
            if (values.All(value => value.HasValue))
                effects[effect] = values.Select(value => value!.Value).ToArray();
        }

        var castTime = CastTime(data);
        var totalTime = castTime;
        // Reconstruction: these five Shaco casts put their gesture total on the wire (the larger of the cast time and the designer total). No shipped
        // script or client code cited here says why; the list predates the C# port, and the sweep's CH-BAR rule accepts the total (batch 386).
        if (name is "Deceive" or "JackInTheBox" or "TwoShivPoison" or "HallucinateFull" or "ShacoBoxSpell")
            totalTime = Math.Max(castTime, DesignerTotalTime(data));

        var spell = new SpellData(
            name, cooldowns.Take(maxRank).ToArray(), mana, effects,
            Math.Max(1.0, Number(data, "SpellData*CastRange", 400.0)), Number(data, "SpellData*CastRadius"),
            Number(data, "SpellData*MissileSpeed", 500.0), Number(data, "SpellData*Coefficient"),
            Number(data, "SpellData*Coefficient2"), maxRank, castTime, totalTime, data);
        _spells.Add(cacheKey, spell);
        return spell;
    }

    /// <param name="liveNames">
    /// The spell each slot holds now (<c>Entity.ActiveSpellNames</c>: <c>BBSetSpell</c>, a pushed character): the cost the stat block shows is the
    /// cost of the spell in the slot, so Lee Sin's Q reads Resonating Strike's 30 energy while its recast window is open. An empty entry or no list
    /// keeps the character's own spell.
    /// </param>
    public IReadOnlyList<double> ManaCostsForRanks(IEnumerable<int> ranks, string championName = "Annie", IReadOnlyList<string>? liveNames = null)
    {
        ArgumentNullException.ThrowIfNull(ranks);
        var costs = new List<double>();
        foreach (var (name, rank) in Champion(championName).SpellNames.Zip(ranks))
        {
            var live = liveNames is not null && costs.Count < liveNames.Count && liveNames[costs.Count] is { Length: > 0 } swapped ? swapped : name;
            var rankCosts = Spell(live, SpellMaxLevel(championName, costs.Count)).ManaCosts;
            var index = Math.Max(0, rank - 1);
            costs.Add(rankCosts[index]);
        }
        return costs;
    }

    public ChampionLevelStats StatsAtLevel(ChampionData champion, int level)
    {
        ArgumentNullException.ThrowIfNull(champion);
        var n = Math.Max(1, level);
        return new ChampionLevelStats(
            champion.BaseHp + champion.HpPerLevel * n,
            champion.BaseMp + champion.MpPerLevel * n,
            champion.BaseDamage + champion.DamagePerLevel * n,
            champion.Armor + champion.ArmorPerLevel * n,
            champion.SpellBlock + champion.SpellBlockPerLevel * n,
            champion.HpRegen + champion.HpRegenPerLevel * n,
            champion.MpRegen + champion.MpRegenPerLevel * n,
            1.0 + champion.AttackSpeedPerLevel / 100.0 * n,
            champion.MoveSpeed, champion.AttackRange);
    }

    /// <summary>A DATA/CFG/defaults/NSEAI.cfg value as the client text parser reads it (section and key are case-insensitive).</summary>
    public InibinValue NseaiValue(string section, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_nseai is null)
        {
            var path = Paths.Data("CFG/defaults/NSEAI.cfg");
            if (!File.Exists(path)) throw new InibinException($"no NSEAI.cfg at {path}");
            _nseai = ClientTextConfig.Load(path);
        }
        return _nseai.TryGet(section, key, out var value)
            ? InibinReader.CoerceString(value)
            : throw new KeyNotFoundException($"NSEAI setting [{section}] {key} was not found");
    }

    private InibinData LoadChampionInibin(string name)
    {
        var path = Paths.Data($"Characters/{name}/{name}.inibin");
        if (!File.Exists(path)) throw new InibinException($"no champion data for '{name}' at {path}");
        return InibinReader.Load(path);
    }

    private IReadOnlyList<float> LoadAttackAnimationProbabilities(string name, InibinData data)
    {
        // The stock character loader loads seven probabilities and counts existing attack
        // spells whose probability is <= 1. The selector scans that prefix.
        string[] slots = ["BaseAttack", "ExtraAttack1", "ExtraAttack2", "ExtraAttack3",
            "CritAttack", "MissAttack", "DodgeAttack"];
        var probabilities = new float[slots.Length];
        int count = 1;
        for (int i = 0; i < slots.Length; i++)
        {
            var key = InibinReader.KeyHash($"Data*{slots[i]}_Probability");
            probabilities[i] = i == 0 ? 1 : 2;
            if (data.TryGetValue(key, out var value))
                probabilities[i] = (float)value.AsClientFloat();
            if (i == 0) continue;
            var spell = String(data, $"Data*{slots[i]}", i < 3 ? $"{name}BasicAttack{i + 1}" : string.Empty);
            if (spell.Length > 0 && File.Exists(Paths.Data($"Spells/{spell}.inibin")) && probabilities[i] <= 1)
                count++;
        }
        return Array.AsReadOnly(probabilities[..count]);
    }

    private readonly Dictionary<string, bool> _attackReveals = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the basic attack of <paramref name="character"/> shows its attacker to the victim's
    /// team. The stock attack path is the same reveal routine as for spells
    /// so it reads the attack spell's own SpellRevealsChampion flag
    /// (absent = true; only MalzaharVoidlingBasicAttack* and MonkeyKingDoubleAttack ship False).
    /// A character with no <c>{character}BasicAttack</c> spell file takes the default.
    /// </summary>
    public bool BasicAttackRevealsChampion(string character)
    {
        if (string.IsNullOrEmpty(character)) return true;
        if (_attackReveals.TryGetValue(character, out var cached)) return cached;
        var path = Paths.Data($"Spells/{character}BasicAttack.inibin");
        var reveals = !File.Exists(path) || SpellData.RevealsChampionFlag(InibinReader.Load(path));
        _attackReveals[character] = reveals;
        return reveals;
    }

    private readonly Dictionary<string, bool> _faceTarget = new(StringComparer.Ordinal);

    /// <summary>
    /// <c>Data*ShouldFaceTarget</c> of a character record (default true): the stock client's
    /// the base unit's facing check (reading
    /// <c>mCharacterData-&gt;bShouldFaceTarget</c>; the loader's own default is 1). Every shipped
    /// champion leaves it out; only <c>OdinNeutralGuardian</c> ships "0". The client's cast animation
    /// gates the turn toward a unit-target on it.
    /// </summary>
    public bool ShouldFaceTarget(string character)
    {
        if (string.IsNullOrEmpty(character)) return true;
        if (_faceTarget.TryGetValue(character, out var cached)) return cached;
        var path = Paths.Data($"Characters/{character}/{character}.inibin");
        var faces = !File.Exists(path) ||
                    !InibinReader.Load(path).TryGetValue(InibinReader.KeyHash("Data*ShouldFaceTarget"), out var value) ||
                    value.AsClientInt() != 0;
        _faceTarget[character] = faces;
        return faces;
    }

    private InibinData LoadSpellInibin(string name)
    {
        var path = Paths.Data($"Spells/{name}.inibin");
        if (!File.Exists(path)) throw new InibinException($"no spell data for '{name}' at {path}");
        return InibinReader.Load(path);
    }

    private InibinData GlobalSpellData()
    {
        if (_globalSpellData is not null) return _globalSpellData;
        var path = Paths.Data("Spells/GlobalSpellData.inibin");
        if (!File.Exists(path)) throw new InibinException($"no global spell data at {path}");
        return _globalSpellData = InibinReader.Load(path);
    }

    /// <summary>
    /// Designer total cast time: GlobalSpellData's SpellDelayTotalTime scaled
    /// by the spell's DelayTotalTimePercent (1.0 s for most gesture spells).
    /// The wire TotalTime keeps its per-spell source rules.
    /// </summary>
    public double DesignerTotalTime(SpellData spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return DesignerTotalTime(spell.Raw);
    }

    private double DesignerTotalTime(InibinData data) =>
        Number(GlobalSpellData(), "Data*SpellDelayTotalTime") *
        (1.0 + Number(data, "SpellData*DelayTotalTimePercent"));

    private double CastTime(InibinData data)
    {
        var overrideCast = OptionalNumber(data, "SpellData*OverrideCastTime");
        if (overrideCast is not null && overrideCast.Value != 0.0) return overrideCast.Value;
        var flags = (long)Number(data, "SpellData*Flags");
        if ((flags & 4) != 0) return 0.25;
        var delay = OptionalNumber(GlobalSpellData(), "Data*SpellDelayCastTime");
        if (delay.HasValue)
            return delay.Value * (1.0 + Number(data, "SpellData*DelayCastOffsetPercent"));
        return Number(data, "SpellData*CastFrame") / 30.0;
    }

    private static List<double> PerRank(InibinData data, string pattern, bool countBooleans = true)
    {
        var values = new List<double>();
        for (var rank = 1; rank <= MaxRanks; rank++)
        {
            var key = string.Format(CultureInfo.InvariantCulture, pattern, rank);
            if (!countBooleans && data.TryGetValue(InibinReader.KeyHash(key), out var raw) && raw.Kind == InibinValueKind.Boolean) break;
            var value = OptionalNumber(data, key);
            if (!value.HasValue) break;
            values.Add(value.Value);
        }
        return values;
    }

    /// <summary>
    /// A number as the client's generic float getter reads it (<see cref="InibinValue.AsClientFloat"/>): the float, fixed-point and
    /// integer segments, a boolean stored 0/1 (ward radius 1, OriannaBall BaseHP=True, turret GoldGivenOnDeath=False), then a text
    /// value through <c>atof</c>. These are present values, not missing keys; only an absent key takes the fallback.
    /// </summary>
    private static double Number(InibinData data, string key, double fallback = 0.0) =>
        OptionalNumber(data, key) ?? fallback;

    private static double? OptionalNumber(InibinData data, string key)
    {
        return data.TryGetValue(InibinReader.KeyHash(key), out var value) &&
               value.Kind is not (InibinValueKind.ByteVector or InibinValueKind.FloatVector)
            ? value.AsClientFloat()
            : null;
    }

    /// <summary>
    /// A spell effect amount. Booleans are the numbers 1 and 0 (the compiler packs those into the boolean segment) and a text value
    /// is kept as text for <see cref="SpellData.EffectNumber"/>, which reads its numeric prefix the way the client does.
    /// </summary>
    private static InibinValue? OptionalValue(InibinData data, string key)
    {
        if (!data.TryGetValue(InibinReader.KeyHash(key), out var value)) return null;
        return value.Kind switch
        {
            InibinValueKind.Boolean => InibinValue.FromDouble(value.Boolean ? 1.0 : 0.0),
            InibinValueKind.ByteVector or InibinValueKind.FloatVector => null,
            _ => value
        };
    }

    private static string String(InibinData data, string key, string fallback)
    {
        return data.TryGetValue(InibinReader.KeyHash(key), out var value) && value.Kind == InibinValueKind.String
            ? value.AsString()
            : fallback;
    }
}
