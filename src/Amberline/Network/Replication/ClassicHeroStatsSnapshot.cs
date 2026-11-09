using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;

namespace Amberline.Network.Replication;

/// <summary>
/// Maps authoritative entity and match counters to the shipped Classic
/// collection. Packet framing remains in <see cref="ClassicHeroStatsPackets"/>.
/// </summary>
public static class ClassicHeroStatsSnapshot
{
    /// <summary>
    /// Builds the narrow public-row compatibility projection used for foreign
    /// heroes.  The native HeroStats constructor starts numeric fields at zero
    /// and strings empty; only the four row counters proven to be consumed from
    /// the collection are overlaid with authoritative values.  Neutral kills
    /// remain on the separate absolute 0xCC field.
    /// </summary>
    public static IReadOnlyDictionary<string, ClassicHeroStatValue> PublicValues(
        Entity hero, ClassicHeroStatsSchema schema)
    {
        ArgumentNullException.ThrowIfNull(hero);
        ArgumentNullException.ThrowIfNull(schema);

        var values = new Dictionary<string, ClassicHeroStatValue>(schema.Definitions.Count,
            StringComparer.Ordinal);
        foreach (var definition in schema.Definitions)
            values[definition.Name] = Default(definition.Kind);

        values["CHAMPIONS_KILLED"] = ClassicHeroStatValue.FromInt(hero.ChampionKills);
        values["NUM_DEATHS"] = ClassicHeroStatValue.FromInt(hero.ChampionDeaths);
        values["ASSISTS"] = ClassicHeroStatValue.FromInt(hero.ChampionAssists);
        values["MINIONS_KILLED"] = ClassicHeroStatValue.FromInt(
            checked(hero.LaneMinionsKilled + hero.OtherMinionsKilled));
        // Batch 518: the ODIN collection's scores are public (every client gets every hero's 0xE2, the scoreboard's "Personal Score"
        // column), so a foreign row keeps them instead of zeroing what the increments added.
        foreach (var (name, value) in OdinScores(hero))
            if (values.ContainsKey(name)) values[name] = value;
        return values;
    }

    // Batch 518: the personal score's parts and total (DominionPlayerScore), as the ODIN collection's FLOAT fields.
    private static IEnumerable<(string Name, ClassicHeroStatValue Value)> OdinScores(Entity hero)
    {
        yield return ("COMBAT_PLAYER_SCORE", ClassicHeroStatValue.FromFloat(hero.OdinCombatScore));
        yield return ("OBJECTIVE_PLAYER_SCORE", ClassicHeroStatValue.FromFloat(hero.OdinObjectiveScore));
        yield return ("TOTAL_PLAYER_SCORE", ClassicHeroStatValue.FromFloat(hero.OdinCombatScore + hero.OdinObjectiveScore));
    }

    /// <summary>
    /// The owner's full row in <paramref name="schema"/>'s collection: every field either mode declares is computed, and the schema's own fields
    /// are kept (CLASSIC on Summoner's Rift, ODIN on map 8).
    /// </summary>
    public static IReadOnlyDictionary<string, ClassicHeroStatValue> Values(
        Entity hero, ClassicHeroStatsSchema schema, double now, ulong? playerId = null, string win = "")
    {
        ArgumentNullException.ThrowIfNull(hero);
        ArgumentNullException.ThrowIfNull(schema);
        if (!double.IsFinite(now) || now < 0.0)
            throw new ArgumentOutOfRangeException(nameof(now));
        ArgumentNullException.ThrowIfNull(win);
        if (win.IndexOf('\0') >= 0)
            throw new ArgumentException("WIN cannot contain NUL.", nameof(win));

        var lifetime = hero.MatchStatistics.Snapshot(now);
        var values = new Dictionary<string, ClassicHeroStatValue>(StringComparer.Ordinal)
        {
            ["FRIENDLY_TURRET_LOST"] = ClassicHeroStatValue.FromInt(lifetime.FriendlyTurretsLost),
            ["TOTAL_UNITS_HEALED"] = ClassicHeroStatValue.FromInt(lifetime.TotalUnitsHealed),
            ["SPELL3_CAST"] = ClassicHeroStatValue.FromInt(lifetime.SpellCasts[2]),
            ["TURRETS_KILLED"] = ClassicHeroStatValue.FromInt(hero.TurretsKilled),
            ["TEAM"] = ClassicHeroStatValue.FromInt(TeamMapping.ToNative(hero.Team)),
            ["TOTAL_DAMAGE_DEALT"] = Float(lifetime.TotalDamageDealt),
            ["EXP"] = Float(hero.Experience),
            ["SPELL4_CAST"] = ClassicHeroStatValue.FromInt(lifetime.SpellCasts[3]),
            // Live rows stay empty.  A terminal owner row may carry the
            // authoritative result policy ("Win"/"Loss") on reconnect.
            ["WIN"] = ClassicHeroStatValue.FromString(win),
            ["LEVEL"] = ClassicHeroStatValue.FromInt(hero.Level),
            ["UNREAL_KILLS"] = ClassicHeroStatValue.FromInt(lifetime.UnrealKills),
            ["TOTAL_TIME_SPENT_DEAD"] = Float(lifetime.TotalTimeSpentDead),
            ["CONSUMABLES_PURCHASED"] = ClassicHeroStatValue.FromInt(lifetime.ConsumablesPurchased),
            ["WAS_AFK"] = ClassicHeroStatValue.FromInt(lifetime.WasAfk ? 1 : 0),
            ["ASSISTS"] = ClassicHeroStatValue.FromInt(hero.ChampionAssists),
            ["LARGEST_KILLING_SPREE"] = ClassicHeroStatValue.FromInt(hero.LargestKillingSpree),
            ["TIME_PLAYED"] = Float(lifetime.TimePlayed),
            ["TOTAL_HEAL"] = ClassicHeroStatValue.FromInt(lifetime.TotalHeal),
            ["GOLD_SPENT"] = Float(lifetime.GoldSpent),
            ["FRIENDLY_HQ_LOST"] = ClassicHeroStatValue.FromInt(lifetime.FriendlyNexusesLost),
            ["LARGEST_CRITICAL_STRIKE"] = Float(lifetime.LargestCriticalStrike),
            ["ITEMS_PURCHASED"] = ClassicHeroStatValue.FromInt(lifetime.ItemsPurchased),
            ["NEUTRAL_MINIONS_KILLED"] = ClassicHeroStatValue.FromInt(hero.NeutralMinionsKilled),
            ["LARGEST_MULTI_KILL"] = ClassicHeroStatValue.FromInt(lifetime.LargestMultiKill),
            ["DOUBLE_KILLS"] = ClassicHeroStatValue.FromInt(lifetime.DoubleKills),
            ["MINIONS_KILLED"] = ClassicHeroStatValue.FromInt(checked(hero.LaneMinionsKilled + hero.OtherMinionsKilled)),
            ["KILLING_SPREES"] = ClassicHeroStatValue.FromInt(hero.KillingSprees),
            ["BARRACKS_KILLED"] = ClassicHeroStatValue.FromInt(hero.InhibitorsKilled),
            ["HQ_KILLED"] = ClassicHeroStatValue.FromInt(hero.NexusesKilled),
            ["NUM_DEATHS"] = ClassicHeroStatValue.FromInt(hero.ChampionDeaths),
            ["TOTAL_DAMAGE_TAKEN"] = Float(lifetime.TotalDamageTaken),
            ["LONGEST_TIME_SPENT_LIVING"] = Float(lifetime.LongestTimeSpentLiving),
            ["PENTA_KILLS"] = ClassicHeroStatValue.FromInt(lifetime.PentaKills),
            ["PHYSICAL_DAMAGE_TAKEN"] = Float(lifetime.PhysicalDamageTaken),
            ["TRIPLE_KILLS"] = ClassicHeroStatValue.FromInt(lifetime.TripleKills),
            ["TIME_SPENT_DISCONNECTED"] = Float(lifetime.TimeSpentDisconnected),
            ["MAGIC_DAMAGE_TAKEN"] = Float(lifetime.MagicDamageTaken),
            // Patch 1.0.0.126's declared LONGLONG field is initialized with
            // an INT holder and therefore has a checked signed-32 wire slot.
            // Preserve roster IDs when representable; retain the native zero
            // for an otherwise valid 64-bit ID that this shipped holder cannot
            // encode without turning it into a negative identity.
            ["ID"] = ClassicHeroStatValue.FromLongLong(NativePlayerId(playerId)),
            ["CHAMPIONS_KILLED"] = ClassicHeroStatValue.FromInt(hero.ChampionKills),
            ["QUADRA_KILLS"] = ClassicHeroStatValue.FromInt(lifetime.QuadraKills),
            ["FRIENDLY_DAMPEN_LOST"] = ClassicHeroStatValue.FromInt(lifetime.FriendlyInhibitorsLost),
            ["SPELL1_CAST"] = ClassicHeroStatValue.FromInt(lifetime.SpellCasts[0]),
            ["SKIN"] = ClassicHeroStatValue.FromInt(hero.SkinId),
            ["SUMMON_SPELL1_CAST"] = ClassicHeroStatValue.FromInt(lifetime.SummonerCasts[0]),
            ["PHYSICAL_DAMAGE_DEALT_PLAYER"] = Float(lifetime.PhysicalDamageDealt),
            ["SUPER_MONSTER_KILLED"] = ClassicHeroStatValue.FromInt(lifetime.SuperMonsterKills),
            ["GOLD_EARNED"] = Float(lifetime.GoldEarned),
            ["SPELL2_CAST"] = ClassicHeroStatValue.FromInt(lifetime.SpellCasts[1]),
            ["NAME"] = ClassicHeroStatValue.FromString(hero.Name),
            ["TIME_OF_FROM_LAST_DISCONNECT"] = Float(lifetime.TimeSinceLastDisconnect),
            ["SUMMON_SPELL2_CAST"] = ClassicHeroStatValue.FromInt(lifetime.SummonerCasts[1]),
            ["MAGIC_DAMAGE_DEALT_PLAYER"] = Float(lifetime.MagicDamageDealt),
            // The ODIN collection (ODIN_Stats.inibin). Captures and neutralisations are counted by the capture leaves (BehaviorLeaves.Capture).
            ["NODE_CAPTURE"] = ClassicHeroStatValue.FromInt(hero.NodeCaptures),
            ["NODE_NEUTRALIZE"] = ClassicHeroStatValue.FromInt(hero.NodeNeutralizes),
            // Batch 518: the assists, the team objective and the scores are the personal score's (DominionPlayerScore), so a 0x4B restates
            // what its 0xE2 and 0xE3 increments added. Reconstruction: the rank and the victory points keep the client's zero (its HeroStats
            // constructor's numeric default); the client ranks by its own scores (the score controller's rank lookup).
            ["NODE_CAPTURE_ASSIST"] = ClassicHeroStatValue.FromInt(hero.NodeCaptureAssists),
            ["NODE_NEUTRALIZE_ASSIST"] = ClassicHeroStatValue.FromInt(hero.NodeNeutralizeAssists),
            ["TEAM_OBJECTIVE"] = ClassicHeroStatValue.FromInt(hero.TeamObjectives),
            ["TOTAL_SCORE_RANK"] = ClassicHeroStatValue.FromInt(0),
            ["VICTORY_POINT_TOTAL"] = ClassicHeroStatValue.FromFloat(0.0f),
        };
        foreach (var (name, value) in OdinScores(hero)) values[name] = value;

        for (var index = 0; index < 6; index++)
        {
            var itemId = index < hero.Inventory.Count ? hero.Inventory[index]?.ItemId ?? 0 : 0;
            values[$"ITEM{index}"] = ClassicHeroStatValue.FromInt(itemId);
        }
        var row = new Dictionary<string, ClassicHeroStatValue>(schema.Definitions.Count, StringComparer.Ordinal);
        foreach (var definition in schema.Definitions)
            row[definition.Name] = values[definition.Name];
        return row;
    }

    private static ClassicHeroStatValue Float(double value)
    {
        if (!double.IsFinite(value) || value < float.MinValue || value > float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value));
        return ClassicHeroStatValue.FromFloat((float)value);
    }

    private static long NativePlayerId(ulong? playerId) =>
        playerId is ulong id && id <= int.MaxValue ? (long)id : 0L;

    private static ClassicHeroStatValue Default(ClassicHeroStatKind kind) => kind switch
    {
        ClassicHeroStatKind.Int => ClassicHeroStatValue.FromInt(0),
        ClassicHeroStatKind.Float => ClassicHeroStatValue.FromFloat(0.0f),
        ClassicHeroStatKind.String => ClassicHeroStatValue.FromString(string.Empty),
        ClassicHeroStatKind.LongLong => ClassicHeroStatValue.FromLongLong(0L),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
