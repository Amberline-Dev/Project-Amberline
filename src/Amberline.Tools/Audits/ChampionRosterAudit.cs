using System.Text.Json;
using Amberline.Data;

namespace Amberline.Tools.Audits;

public sealed record ChampionSpellAudit(
    string Name,
    int MaxRank,
    IReadOnlyList<double> Cooldowns,
    IReadOnlyList<double> ManaCosts,
    IReadOnlyDictionary<int, IReadOnlyList<double>> Effects,
    double CastRange,
    double CastRadius,
    double MissileSpeed,
    double Coefficient,
    double Coefficient2,
    double CastTime,
    double TotalTime,
    LuaLoadStatus ScriptStatus,
    string? ScriptError);

public sealed record ChampionStatsAudit(
    double MaxHp, double MaxMp, double AttackDamage, double Armor,
    double SpellBlock, double HpRegen, double MpRegen, double AttackSpeedMod,
    double MoveSpeed, double AttackRange);

public sealed record ChampionRosterEntry(
    string Name,
    string Role,
    ChampionStatsAudit StatsLevel1,
    IReadOnlyList<ChampionSpellAudit> Spells,
    LuaLoadStatus CharacterScriptStatus,
    string? CharacterScriptError,
    bool IsCougar = false,
    IReadOnlyList<string>? RelatedScripts = null);

public sealed record ChampionRosterAuditReport(
    IReadOnlyList<ChampionRosterEntry> Champions,
    ChampionRosterEntry? NidaleeCougar,
    int CharacterScriptErrors,
    int SpellScriptErrors)
{
    public int Count => Champions.Count;

    public string ToJson(bool indented = true) => JsonSerializer.Serialize(this,
        new JsonSerializerOptions { WriteIndented = indented });
}

/// <summary>Read-only inventory of the maintained champion roster.</summary>
public static class ChampionRosterAudit
{
    public static ChampionRosterAuditReport Inventory(GameDataPaths? paths = null)
    {
        var data = new GameData(paths ?? GameDataPaths.Default);
        var entries = new List<ChampionRosterEntry>();
        foreach (var champion in ChampionCatalog.Playable)
            entries.Add(ReadChampion(data, champion.Name, champion.Role!, isCougar: false));

        var cougar = ReadChampion(data, "Nidalee_Cougar", "top", isCougar: true);
        var result = entries.Select(entry => entry with
        {
            RelatedScripts = entry.Name == "Orianna"
                ? LuaNames(data.Paths, "Spells/Ori*.luaobj")
                : entry.Name == "Nidalee"
                    ? ["Takedown", "Pounce", "Swipe", "Prowl"]
                    : entry.RelatedScripts,
        }).ToArray();
        var all = result.Append(cougar).ToArray();
        return new ChampionRosterAuditReport(result, cougar,
            all.Count(entry => entry.CharacterScriptStatus != LuaLoadStatus.Success),
            all.Sum(entry => entry.Spells.Count(spell => spell.ScriptStatus != LuaLoadStatus.Success)));
    }

    private static ChampionRosterEntry ReadChampion(GameData data, string name,
        string role, bool isCougar)
    {
        var champion = data.Champion(name);
        var stats = data.StatsAtLevel(champion, 1);
        var spells = champion.SpellNames.Select((spellName, slot) =>
        {
            // The slot's rank limit is the spellbook rule (5, 5, 5, 3, or the character's Data*MaxLevels: Udyr's R has five ranks).
            var spell = data.Spell(spellName, isCougar ? 3 : data.SpellMaxLevel(name, slot));
            var script = LuaObjectReader.LoadData(data.Paths, $"Spells/{spellName}.luaobj");
            // The corpus lists an effect only if every rank is a number: XenZhaoSweep's effect 4 (the slow's duration) ships as the text "1.5.0000".
            var effects = spell.Effects.Where(pair => pair.Value.All(value => value.IsNumeric)).ToDictionary(pair => pair.Key,
                pair => (IReadOnlyList<double>)pair.Value.Select(value => value.AsDouble()).ToArray());
            return new ChampionSpellAudit(spellName, spell.MaxRank, spell.Cooldowns,
                spell.ManaCosts, effects, spell.CastRange, spell.CastRadius,
                spell.MissileSpeed, spell.Coefficient, spell.Coefficient2,
                spell.CastTime, spell.TotalTime, script.Status, script.Error ?? script.Reason);
        }).ToArray();
        var character = LuaObjectReader.LoadData(data.Paths,
            $"Characters/{name}/Scripts/CharScript{name}.luaobj");
        return new ChampionRosterEntry(name, role,
            new ChampionStatsAudit(stats.MaxHp, stats.MaxMp, stats.AttackDamage,
                stats.Armor, stats.SpellBlock, stats.HpRegen, stats.MpRegen,
                stats.AttackSpeedMod, stats.MoveSpeed, stats.AttackRange), spells,
            character.Status, character.Error ?? character.Reason, isCougar);
    }

    private static IReadOnlyList<string> LuaNames(GameDataPaths paths, string pattern)
    {
        var directory = Path.GetDirectoryName(paths.Data(pattern))!;
        var filePattern = Path.GetFileName(pattern);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, filePattern).OrderBy(path => path)
                .Select(Path.GetFileNameWithoutExtension).OfType<string>().ToArray()
            : Array.Empty<string>();
    }
}
