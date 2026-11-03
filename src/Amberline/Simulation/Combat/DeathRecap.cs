using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;

namespace Amberline.Simulation.Combat;

/// <summary>Event-source values used by the patch-126 ParamsDamage record.</summary>
public enum DeathRecapEventSource : byte
{
    Spell = 0,
    Item = 1,
    Buff = 2,
    Character = 3,
    BasicAttack = 4,
    Cheat = 5,
}

/// <summary>Compact source-type values used by the patch-126 death recap.</summary>
public enum DeathRecapSourceType : byte
{
    Hero = 0,
    Minion = 1,
    Tower = 2,
    Pet = 3,
    Clone = 4,
    Unknown = 5,
}

/// <summary>
/// Typed origin metadata supplied by a damage producer. A named spell, item,
/// or buff must provide its own hash at the hit boundary; this type deliberately
/// has no last-cast lookup or other asynchronous inference.
/// Learned ranks are captured at the damage boundary and written unchanged to
/// the compact native field. The HUD uses that field for event prose, where
/// the matched consumer sign-extends its three bits; that display quirk does
/// not gate the underlying recap row.
/// </summary>
public readonly record struct DeathRecapOrigin(
    DeathRecapEventSource EventSource,
    DeathRecapEventSource ParentEventSource,
    uint SourceScriptNameHash,
    uint ParentScriptNameHash,
    uint ParentCasterNetId,
    byte ParentTeam,
    DeathRecapSourceType ParentSourceType,
    byte SourceSpellLevel = 0)
{
    // A champion's row is named by what the scripts' damage carries (BbHookFrame.SpellSlot/SpellRank and
    // BbSpellHost.RecapOrigin, batches 148/149/154): ChampionSpell hashes the shipped spell name of the slot the
    // cast ran from. The hand-written per-champion rows that named the same spells by hand (Annie's and Blitzcrank's
    // direct R aliases and the fixed Ezreal/Orianna/Annie/Blitzcrank hashes) are gone with them, as Lee Sin's was in
    // batch 105: ChampionSpell(lee, 3, rank) already produced the identical hash.
    public static readonly uint SummonerDotHash =
        ProtocolEncoding.HashString("SummonerDot");
    public static readonly uint SummonerSmiteHash =
        ProtocolEncoding.HashString("SummonerSmite");
    public static readonly uint OriannaSpellSwordHash =
        ProtocolEncoding.HashString("OrianaSpellSword");

    private static readonly IReadOnlyDictionary<uint, string> SummonerSpellOwners =
        new Dictionary<uint, string>
        {
            [SummonerDotHash] = "SummonerDot",
            [SummonerSmiteHash] = "SummonerSmite",
        };

    // The client's item name map hashes the decimal item number.
    // These are the shipped items whose damage producers supply this origin.
    private static readonly HashSet<uint> ItemHashes = new[]
        { 3077, 3091, 3114, 3126, 3131, 3106, 3128, 3144, 3146, 3123,
          3068, 3075, 3154, 3087, 3178, 3180, 3184, 3186 }
        .Select(id => ProtocolEncoding.HashString(id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        .ToHashSet();

    internal static readonly IReadOnlyList<string> ChampionNames =
        ChampionCatalog.PlayableNames.Order(StringComparer.Ordinal).ToArray();

    private static readonly Lazy<Dictionary<uint, string>> SpellOwners = new(() =>
        ChampionNames
            .SelectMany(champion => GameData.Default.Champion(champion).SpellNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => (Hash: ProtocolEncoding.HashString(name), Champion: champion)))
            .ToDictionary(pair => pair.Hash, pair => pair.Champion));

    // The champion spell catalogs use the retail spellbook limits (GameData.SpellMaxLevel: rank-five Q/W/E, rank-three R, or the
    // character's Data*MaxLevels). The transformed Cougar entries are all rank-three. Keep these limits beside the shipped names rather
    // than inferring them from mana-array lengths.
    private static readonly Lazy<Dictionary<uint, int>> SpellMaxRanks = new(() =>
        ChampionNames
            .SelectMany(champion => GameData.Default.Champion(champion).SpellNames
                .Select((name, slot) => (Name: name, Slot: slot))
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Name))
                .Select(pair => (Hash: ProtocolEncoding.HashString(pair.Name),
                    MaxRank: GameData.Default.SpellMaxLevel(champion, pair.Slot))))
            .Concat(GameData.Default.Champion("Nidalee_Cougar").SpellNames
                .Where(name => name is "Takedown" or "Pounce" or "Swipe")
                .Select(name => (Hash: ProtocolEncoding.HashString(name), MaxRank: 3)))
            .ToDictionary(pair => pair.Hash, pair => pair.MaxRank));

    // Cougar Q/W/E are separate shipped spell records. Keep them out of the
    // human ChampionSpell lookup so a transformed cast cannot be mislabeled
    // with JavelinToss, Bushwhack, or PrimalSurge.
    private static readonly Lazy<Dictionary<uint, string>> FormSpellOwners = new(() =>
        GameData.Default.Champion("Nidalee_Cougar").SpellNames
            .Where(name => name is "Takedown" or "Pounce" or "Swipe")
            .Select(name => (Hash: ProtocolEncoding.HashString(name), Champion: "Nidalee"))
            .ToDictionary(pair => pair.Hash, pair => pair.Champion));

    // These scripts are shipped buff producers rather than castable entries.
    // Keep this catalog explicit so a proc cannot smuggle an arbitrary hash
    // into the Buff event-source branch.
    private static readonly Lazy<Dictionary<uint, string>> BuffOwners = new(() =>
        new[] { (Hash: OriannaSpellSwordHash, Champion: "Orianna") }
            .ToDictionary(pair => pair.Hash, pair => pair.Champion));

    // Shipped TargetExecute scripts versus buff-deactivation/periodic damage.
    // Use the character root, as for the existing named producers; do not
    // invent an upstream cast parent for a buff callback.
    private static readonly Lazy<Dictionary<uint, (string Name, DeathRecapEventSource Event)>> WorldScripts = new(() =>
        new[] { "WormAttack", "SweepingBlow", "WrathDamage", "Propel", "Burning", "SummonerFortify" }
            // Batch 192: a shipped spell with no client buff (SweepingBlow) is a known origin too. The client-buff gate alone left its
            // damage Unclassified, so the Sweep dropped out of the death recap; its name is the script's own, as for WormAttack.
            .Where(name => GameData.Default.HasClientBuff(name) || GameData.Default.HasSpell(name))
            .ToDictionary(ProtocolEncoding.HashString, name => (name,
                name is "WormAttack" or "SweepingBlow"
                    ? DeathRecapEventSource.Spell : DeathRecapEventSource.Buff)));

    public static DeathRecapOrigin WorldScript(Entity caster, string scriptName)
    {
        var hash = ProtocolEncoding.HashString(scriptName);
        if (!WorldScripts.Value.TryGetValue(hash, out var script)) return Unclassified;
        var type = WorldScriptSourceType(caster, script.Name);
        var origin = new DeathRecapOrigin(script.Event, DeathRecapEventSource.Character,
            hash, 0, caster.NetId, (byte)caster.Team, type);
        return origin.IsSafeForSource(caster, type) ? origin : Unclassified;
    }

    private static DeathRecapSourceType WorldScriptSourceType(Entity source, string scriptName) =>
        scriptName switch
        {
            "WormAttack" or "SweepingBlow" or "WrathDamage" or "Propel"
                when source.Kind == EntityKind.Neutral && source.UnitType == UnitType.Monster &&
                     source.Team == Team.Neutral && source.Name == "Worm" => DeathRecapSourceType.Minion,
            "Burning" when source.Kind == EntityKind.Neutral && source.UnitType == UnitType.Monster &&
                           source.Team == Team.Neutral && source.Name == "Dragon" => DeathRecapSourceType.Minion,
            "Burning" when source.Kind == EntityKind.Hero && source.UnitType == UnitType.Hero &&
                           source.Team is Team.Order or Team.Chaos => DeathRecapSourceType.Hero,
            "SummonerFortify" when source.UnitType == UnitType.Turret &&
                                  source.Team is Team.Order or Team.Chaos => DeathRecapSourceType.Tower,
            _ => DeathRecapSourceType.Unknown,
        };

    public static DeathRecapOrigin ChampionSpell(Entity caster, int slot, int rank)
    {
        if (slot is < 0 or > 3 || caster.Team is not (Team.Order or Team.Chaos))
            return Unclassified;
        var names = GameData.Default.Champion(caster.ChampionName).SpellNames;
        if (slot >= names.Count || string.IsNullOrEmpty(names[slot])) return Unclassified;
        // The name the slot holds now: a character layer a script pushed with its spells (Nidalee's cougar) replaces the champion's own, and
        // the spell is then one of the form's (Takedown, Pounce, Swipe) with the level the slot's rank gives it.
        if (slot < caster.ActiveSpellNames.Count && !string.IsNullOrEmpty(caster.ActiveSpellNames[slot]) &&
            !string.Equals(caster.ActiveSpellNames[slot], names[slot], StringComparison.Ordinal))
            return ChampionFormSpell(caster, caster.ActiveSpellNames[slot], rank);
        var hash = ProtocolEncoding.HashString(names[slot]);
        if (!SpellOwners.Value.TryGetValue(hash, out var champion) || champion != caster.ChampionName ||
            !SpellMaxRanks.Value.TryGetValue(hash, out var maxRank) || rank is < 1 || rank > maxRank)
            return Unclassified;
        return new(DeathRecapEventSource.Spell, DeathRecapEventSource.Character,
            hash, 0, caster.NetId, (byte)caster.Team, DeathRecapSourceType.Hero, (byte)rank);
    }

    /// <summary>
    /// Creates a catalog-backed origin for a transformed champion spell whose
    /// active name is not in the human champion spellbook.
    /// </summary>
    public static DeathRecapOrigin ChampionFormSpell(Entity caster, string spellName, int rank)
    {
        ArgumentNullException.ThrowIfNull(caster);
        if (rank is < 1 or > 5 || caster.Team is not (Team.Order or Team.Chaos) ||
            !string.Equals(caster.ChampionName, "Nidalee", StringComparison.OrdinalIgnoreCase))
            return Unclassified;
        var names = GameData.Default.Champion("Nidalee_Cougar").SpellNames;
        if (!names.Contains(spellName, StringComparer.Ordinal) ||
            !FormSpellOwners.Value.TryGetValue(ProtocolEncoding.HashString(spellName), out var champion) ||
            !string.Equals(champion, caster.ChampionName, StringComparison.OrdinalIgnoreCase) ||
            !SpellMaxRanks.Value.TryGetValue(ProtocolEncoding.HashString(spellName), out var maxRank))
            return Unclassified;
        // Reconstruction: the form's spells have three ranks while their slots keep the human spell's rank (up to five): the level of the
        // recap row is the slot's rank, capped at the form spell's last.
        rank = Math.Min(rank, maxRank);
        return new(DeathRecapEventSource.Spell, DeathRecapEventSource.Character,
            ProtocolEncoding.HashString(spellName), 0, caster.NetId, (byte)caster.Team,
            DeathRecapSourceType.Hero, (byte)rank);
    }

    public static DeathRecapOrigin Item(Entity caster, int itemId)
    {
        var hash = ProtocolEncoding.HashString(itemId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var type = caster.Kind == EntityKind.ShacoClone ? DeathRecapSourceType.Clone : DeathRecapSourceType.Hero;
        if (!ItemHashes.Contains(hash) || (caster.UnitType != UnitType.Hero && caster.Kind != EntityKind.ShacoClone) ||
            caster.Team is not (Team.Order or Team.Chaos)) return Unclassified;
        return new(DeathRecapEventSource.Item, DeathRecapEventSource.Character,
            hash, 0, caster.NetId, (byte)caster.Team, type);
    }

    public static DeathRecapOrigin BasicAttack(byte parentTeam, DeathRecapSourceType sourceType) =>
        new(DeathRecapEventSource.BasicAttack, DeathRecapEventSource.BasicAttack,
            0, 0, 0, parentTeam, sourceType);

    /// <summary>
    /// The origin of the damage a hero's own buff script deals when that script is a catalog-backed buff producer (Orianna's Clockwork
    /// Windup proc: OrianaSpellSword), else null (the recap then names nothing). The catalog is explicit on purpose, see <c>BuffOwners</c>.
    /// </summary>
    public static DeathRecapOrigin? BuffProducer(Entity caster, string scriptName)
    {
        ArgumentNullException.ThrowIfNull(caster);
        var hash = ProtocolEncoding.HashString(scriptName);
        if (caster.UnitType != UnitType.Hero || caster.NetId == 0 || !BuffOwners.Value.TryGetValue(hash, out var champion) ||
            !string.Equals(caster.ChampionName, champion, StringComparison.OrdinalIgnoreCase)) return null;
        byte team;
        switch (caster.Team)
        {
            case Team.Order: team = 0; break;
            case Team.Chaos: team = 1; break;
            default: return null;
        }
        return new(DeathRecapEventSource.Buff, DeathRecapEventSource.Character,
            hash, 0, caster.NetId, team, DeathRecapSourceType.Hero);
    }

    // Reconstruction: the spell a pet casts from its own slot is named in the recap after the owner's spell that made the pet, at the owner's
    // rank when the damage lands (the hand-written Jack in the Box did the same: JackInTheBox, W). The catalog is explicit on purpose.
    private static readonly IReadOnlyDictionary<string, (string Champion, int Slot)> PetSpellParents =
        new Dictionary<string, (string, int)>(StringComparer.Ordinal) { ["ShacoBoxSpell"] = ("Shaco", 1) };

    /// <summary>The origin of the damage a hero's pet deals with a spell of its own (ShacoBoxSpell), else null: the owner's spell that made the pet.</summary>
    public static DeathRecapOrigin? PetSpell(Entity owner, string scriptName)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.UnitType != UnitType.Hero || !PetSpellParents.TryGetValue(scriptName, out var parent) ||
            !string.Equals(owner.ChampionName, parent.Champion, StringComparison.OrdinalIgnoreCase) ||
            parent.Slot >= owner.SpellRanks.Count) return null;
        return ChampionSpell(owner, parent.Slot, owner.SpellRanks[parent.Slot]);
    }

    public static DeathRecapOrigin SummonerSpell(Entity caster, string spellName)
    {
        ArgumentNullException.ThrowIfNull(caster);
        ArgumentNullException.ThrowIfNull(spellName);
        if (caster.NetId == 0 || caster.Team is not (Team.Order or Team.Chaos))
            return Unclassified;
        var hash = ProtocolEncoding.HashString(spellName);
        return SummonerSpellOwners.ContainsKey(hash)
            ? new(DeathRecapEventSource.Spell, DeathRecapEventSource.Character,
                hash, 0, caster.NetId, (byte)caster.Team, DeathRecapSourceType.Hero)
            : Unclassified;
    }

    public static DeathRecapOrigin SummonerDot(Entity caster) =>
        SummonerSpell(caster, "SummonerDot");

    public static DeathRecapOrigin SummonerSmite(Entity caster) =>
        SummonerSpell(caster, "SummonerSmite");

    /// <summary>
    /// Explicitly suppresses recap classification for a damage boundary that
    /// has no proven native origin. This is distinct from a null origin:
    /// CombatService may infer BasicAttack only for its actual auto-attack
    /// producer when the origin is null.
    /// </summary>
    public static DeathRecapOrigin Unclassified => default;

    public bool IsSafe
    {
        get
        {
            if (ParentTeam > 2 || ParentSourceType > DeathRecapSourceType.Clone ||
                SourceSpellLevel > 7)
                return false;
            if (EventSource == DeathRecapEventSource.BasicAttack)
                return ParentEventSource == DeathRecapEventSource.BasicAttack &&
                    SourceScriptNameHash == 0 && ParentScriptNameHash == 0 &&
                    ParentCasterNetId == 0 && SourceSpellLevel == 0;

            if (WorldScripts.Value.TryGetValue(SourceScriptNameHash, out var script))
                return EventSource == script.Event && ParentEventSource == DeathRecapEventSource.Character &&
                    ParentScriptNameHash == 0 && ParentCasterNetId != 0 && SourceSpellLevel == 0 &&
                    (script.Name == "SummonerFortify"
                        ? ParentSourceType == DeathRecapSourceType.Tower && ParentTeam <= 1
                        : ParentSourceType == DeathRecapSourceType.Minion && ParentTeam == 2 ||
                          script.Name == "Burning" && ParentSourceType == DeathRecapSourceType.Hero && ParentTeam <= 1);

            if (EventSource == DeathRecapEventSource.Item)
                return ParentEventSource == DeathRecapEventSource.Character && ParentScriptNameHash == 0 &&
                    ParentCasterNetId != 0 && ParentTeam <= 1 &&
                    ParentSourceType is DeathRecapSourceType.Hero or DeathRecapSourceType.Clone &&
                    SourceSpellLevel == 0 && ItemHashes.Contains(SourceScriptNameHash);

            if (EventSource == DeathRecapEventSource.Buff)
                return ParentEventSource == DeathRecapEventSource.Character && ParentScriptNameHash == 0 &&
                    ParentCasterNetId != 0 && ParentTeam <= 1 && ParentSourceType == DeathRecapSourceType.Hero &&
                    SourceSpellLevel == 0 && BuffOwners.Value.ContainsKey(SourceScriptNameHash);

            // This is deliberately a bounded first producer batch. The stock
            // Spell branch requires a catalog-backed source hash; its
            // Character parent branch formats as Passive, which the stock
            // recap formatter replaces with SpellName. Do not accept an
            // arbitrary nonzero hash or a guessed parent pairing here.
            if (EventSource != DeathRecapEventSource.Spell ||
                ParentEventSource != DeathRecapEventSource.Character ||
                ParentScriptNameHash != 0 || ParentCasterNetId == 0 ||
                ParentTeam > 1 || ParentSourceType != DeathRecapSourceType.Hero)
                return false;
            if (SummonerSpellOwners.ContainsKey(SourceScriptNameHash))
                return SourceSpellLevel == 0;
            return SpellMaxRanks.Value.TryGetValue(SourceScriptNameHash, out var maxRank) &&
                SourceSpellLevel >= 1 && SourceSpellLevel <= maxRank &&
                (SpellOwners.Value.ContainsKey(SourceScriptNameHash) ||
                 FormSpellOwners.Value.ContainsKey(SourceScriptNameHash));
        }
    }

    /// <summary>Validates producer ownership against the live source entity.</summary>
    public bool IsSafeForSource(Entity source, DeathRecapSourceType sourceType)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!IsSafe) return false;
        if (WorldScripts.Value.TryGetValue(SourceScriptNameHash, out var script))
            return ParentCasterNetId == source.NetId && ParentTeam == (byte)source.Team &&
                ParentSourceType == sourceType && sourceType == WorldScriptSourceType(source, script.Name);
        if (EventSource == DeathRecapEventSource.BasicAttack)
            return source.Team switch
            {
                Team.Order => ParentTeam == 0,
                Team.Chaos => ParentTeam == 1,
                Team.Neutral => ParentTeam == 2,
                _ => false,
            } && ParentSourceType == sourceType;
        if (EventSource == DeathRecapEventSource.Item)
            return (sourceType == DeathRecapSourceType.Hero ||
                    sourceType == DeathRecapSourceType.Clone && source.Kind == EntityKind.ShacoClone) &&
                ParentSourceType == sourceType && ParentCasterNetId == source.NetId &&
                ParentTeam == (byte)source.Team;
        if (EventSource == DeathRecapEventSource.Buff)
            return sourceType == DeathRecapSourceType.Hero && ParentCasterNetId == source.NetId &&
                ParentTeam == (source.Team == Team.Order ? 0 : source.Team == Team.Chaos ? 1 : byte.MaxValue) &&
                BuffOwners.Value.TryGetValue(SourceScriptNameHash, out var buffChampion) &&
                string.Equals(source.ChampionName, buffChampion, StringComparison.OrdinalIgnoreCase);
        if (EventSource != DeathRecapEventSource.Spell ||
            sourceType != DeathRecapSourceType.Hero || ParentCasterNetId != source.NetId ||
            ParentTeam != (source.Team == Team.Order ? 0 : source.Team == Team.Chaos ? 1 : byte.MaxValue))
            return false;
        if (SummonerSpellOwners.ContainsKey(SourceScriptNameHash)) return true;
        return (SpellOwners.Value.TryGetValue(SourceScriptNameHash, out var champion) ||
                FormSpellOwners.Value.TryGetValue(SourceScriptNameHash, out champion)) &&
            string.Equals(source.ChampionName, champion, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>One final-resolved damage contribution for a victim.</summary>
/// <param name="ProjectToParentWhenAttackerUnavailable">
/// A validated named pet or clone spell may use its live owner as the recap
/// wire source when the actual attacker retires before delivery. The actual
/// attacker remains in this history record.
/// </param>
public readonly record struct DeathRecapRecord(
    double Timestamp,
    uint VictimNetId,
    uint AttackerNetId,
    double PhysicalDamage,
    double MagicalDamage,
    double TrueDamage,
    DeathRecapOrigin Origin,
    bool ProjectToParentWhenAttackerUnavailable = false)
{
    public double TotalDamage => PhysicalDamage + MagicalDamage + TrueDamage;
}

/// <summary>
/// Immutable state captured at one hero death. It remains available while the
/// hero is dead so a new ready connection can receive it independently.
/// </summary>
public sealed class DeathRecapSnapshot
{
    public DeathRecapSnapshot(
        long generation,
        uint victimNetId,
        uint killerNetId,
        double timeWindow,
        DeathRecapSourceType killerSourceType,
        IReadOnlyList<DeathRecapRecord> records)
    {
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        if (victimNetId == 0) throw new ArgumentOutOfRangeException(nameof(victimNetId));
        if (!double.IsFinite(timeWindow) || timeWindow < 0.0)
            throw new ArgumentOutOfRangeException(nameof(timeWindow));
        ArgumentNullException.ThrowIfNull(records);
        Generation = generation;
        VictimNetId = victimNetId;
        KillerNetId = killerNetId;
        TimeWindow = timeWindow;
        KillerSourceType = killerSourceType;
        Records = records.ToArray();
    }

    public long Generation { get; }
    public uint VictimNetId { get; }
    public uint KillerNetId { get; }
    public double TimeWindow { get; }
    public DeathRecapSourceType KillerSourceType { get; }
    public IReadOnlyList<DeathRecapRecord> Records { get; }
}

/// <summary>Bounded incoming damage history owned by one victim hero.</summary>
internal sealed class DeathRecapHistory
{
    // Shipped Game.cfg minimum (DeathRecapMinimumTimeWindow 15). No extension is applied: its predicate
    // is unrecovered (the 5 s DeathRecapExtensionTimeWindow stays unused).
    // Reconstruction: the fixed-window selection and the 128-record cap are emulator policy (retail's extension predicate is unrecovered). The client keeps its
    // own per-hero attacker list (its damage-taken handler, cap about 100 entries), which holds
    // neither the recap origins nor the rows this history sends, so that number does not carry over.
    public const double WindowSeconds = 15.0;
    public const int MaximumRecords = 128;

    private readonly List<DeathRecapRecord> _records = new(MaximumRecords);

    public void Add(DeathRecapRecord record, double now)
    {
        Trim(now);
        _records.Add(record);
        if (_records.Count > MaximumRecords)
            _records.RemoveRange(0, _records.Count - MaximumRecords);
    }

    public IReadOnlyList<DeathRecapRecord> Snapshot(double now)
    {
        Trim(now);
        return _records.ToArray();
    }

    public void Clear() => _records.Clear();

    private void Trim(double now)
    {
        var first = 0;
        while (first < _records.Count && now - _records[first].Timestamp > WindowSeconds)
            first++;
        if (first > 0) _records.RemoveRange(0, first);
    }
}
