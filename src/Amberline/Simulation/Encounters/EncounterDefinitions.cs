using System.Text.Json;
using Amberline.Data;

namespace Amberline.Simulation.Encounters;

/// <summary>
/// One monster line of an encounter group: the character record it spawns, how many, its AI flag and whether a team can target it.
/// <c>BehaviorTreeAi</c> is the shipped field as read (1 on the capture-point guardian, 0 on the scripted units); its meaning is
/// Reconstruction (batch 446 part 1). An absent <c>Targetable</c> reads as targetable.
/// Batch 451: <c>OrderSkinName</c> and <c>ChaosSkinName</c> are the team's own character (the Odin waves name only these; SmallWave.json).
/// Batch 476d: <c>MinimapIcon</c> names the minimap.ini section the unit's icon is drawn from (OdinCenterRelic_Left: CenterRelicLeft).
/// Batch 499: <c>Tags</c> are what a mutator's Tags select (SmallWave.json: the casters "Melee", the superminion "SuperMinion");
/// <c>GoldAwarded</c> and <c>ExperienceAwarded</c> are the unit's bounty when the file names them (null when it does not).
/// </summary>
public sealed record EncounterMonster(string SkinName, double Value, int Count, int BehaviorTreeAi, bool Targetable,
    string OrderSkinName = "", string ChaosSkinName = "", string MinimapIcon = "", IReadOnlyList<string>? Tags = null,
    double? GoldAwarded = null, double? ExperienceAwarded = null);

/// <summary>One monster group of an encounter definition.</summary>
public sealed record EncounterMonsterGroup(IReadOnlyList<EncounterMonster> Monsters);

/// <summary>
/// An <c>EncounterDefinition</c> file: <c>LEVELS/&lt;map&gt;/Encounters/&lt;Name&gt;.json</c>. <c>SquadAi</c> is the squad's AI name ("Guard" or "Wave").
/// Batch 499: <c>MaxMonstersPerSpawn</c> and <c>SpawnDelay</c> as shipped (SmallWave.json: 1 and 0.75; 0 when the file names none).
/// </summary>
public sealed record EncounterDefinition(string Name, double BonusValue, string SquadAi, IReadOnlyList<EncounterMonsterGroup> MonsterGroups,
    int MaxMonstersPerSpawn = 0, double SpawnDelay = 0.0);

/// <summary>One stat a mutator changes. Batch 499: <c>Min</c> and <c>Max</c> bound the mutated amount (unbounded when the file names none).</summary>
public sealed record MutatorStat(string Stat, double Value, double Min = double.MinValue, double Max = double.MaxValue);

/// <summary>
/// A <c>MutatorDefinition</c> file under <c>Encounters/Mutators</c>: its type and its stat mutations. A ProgressiveMultiply file lists them in
/// <c>Mutations</c> (CapturePointMutator.json, MeleeWaveProgression.json); a StatGraph file lists one set per <c>MutationIndex</c> in
/// <c>MutatedData</c> (MeleeWaveBase.json, index 0), held in <c>Graph</c>.
/// </summary>
public sealed record MutatorDefinition(string Name, string Type, IReadOnlyList<MutatorStat> Mutations,
    IReadOnlyDictionary<int, IReadOnlyList<MutatorStat>>? Graph = null)
{
    /// <summary>
    /// Batch 499 (Reconstruction: the server's mutator code did not ship; the rule is read from the files' shapes and the trees' use of them).
    /// The stat amounts at a mutation index. A StatGraph gives the set its <c>MutatedData</c> holds for that index (MeleeWaveBase index 0:
    /// +50 health, +9 attack damage, +15 magic resist). A ProgressiveMultiply multiplies each <c>Value</c> by the index: the trees add
    /// CapturePointMutator at index 0 ("AntiSnowball", OdinScriptBehaviorTrees.xml:2095-2101) and SpawnMinionWave's "catchup" UpdateMutator sets the
    /// index to the other team's point count (:3851-3878), so each point the enemy holds gives the wave +3.5 attack damage, +4 armour and +3 magic
    /// resist. Each amount is bounded by its Min and Max. PerLevelValue is 0 in every mutator a tree adds and is not read.
    /// </summary>
    public IEnumerable<(string Stat, double Amount)> At(int index) => Type switch
    {
        "StatGraph" => (Graph?.GetValueOrDefault(index) ?? []).Select(stat => (stat.Stat, Math.Clamp(stat.Value, stat.Min, stat.Max))),
        "ProgressiveMultiply" => Mutations.Select(stat => (stat.Stat, Math.Clamp(stat.Value * index, stat.Min, stat.Max))),
        _ => [],
    };
}

/// <summary>
/// The encounter and mutator definitions of one map, read from its shipped files. A file that does not parse is reported in
/// <see cref="Errors"/> and left out; nothing is thrown.
/// </summary>
public sealed class EncounterCatalog
{
    public EncounterCatalog(IReadOnlyDictionary<string, EncounterDefinition> encounters,
        IReadOnlyDictionary<string, MutatorDefinition> mutators, IReadOnlyList<string> errors)
    {
        Encounters = encounters;
        Mutators = mutators;
        Errors = errors;
    }

    /// <summary>The encounter definitions by file name (without the extension).</summary>
    public IReadOnlyDictionary<string, EncounterDefinition> Encounters { get; }

    /// <summary>The mutator definitions by file name (without the extension).</summary>
    public IReadOnlyDictionary<string, MutatorDefinition> Mutators { get; }

    /// <summary>Files that did not parse, each with its path and the reason.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Loads the encounters and mutators of a map (its <c>LEVELS/&lt;map&gt;/Encounters</c> folder).</summary>
    public static EncounterCatalog Load(GameDataPaths map)
    {
        var errors = new List<string>();
        var encounters = new Dictionary<string, EncounterDefinition>(StringComparer.Ordinal);
        var mutators = new Dictionary<string, MutatorDefinition>(StringComparer.Ordinal);
        var folder = map.Under("LEVELS", map.MapName, "Encounters");
        if (!Directory.Exists(folder))
        {
            errors.Add($"{folder}: no Encounters folder");
            return new EncounterCatalog(encounters, mutators, errors);
        }
        foreach (var file in CaseInsensitivePaths.EnumerateFiles(folder, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            try
            {
                encounters[name] = ParseEncounter(name, File.ReadAllText(file));
            }
            catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
            {
                errors.Add($"{file}: {error.Message}");
            }
        }
        var mutatorFolder = CaseInsensitivePaths.Resolve(Path.Combine(folder, "Mutators"));
        if (Directory.Exists(mutatorFolder))
        {
            foreach (var file in CaseInsensitivePaths.EnumerateFiles(mutatorFolder, "*.json").OrderBy(path => path, StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                try
                {
                    mutators[name] = ParseMutator(name, File.ReadAllText(file));
                }
                catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
                {
                    errors.Add($"{file}: {error.Message}");
                }
            }
        }
        return new EncounterCatalog(encounters, mutators, errors);
    }

    private static EncounterDefinition ParseEncounter(string name, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement.GetProperty("EncounterDefinition");
        var groups = new List<EncounterMonsterGroup>();
        foreach (var group in Items(root, "MonsterGroups"))
        {
            var monsters = Items(group, "Monsters").Select(monster => new EncounterMonster(
                Text(monster, "SkinName"),
                Number(monster, "Value"),
                (int)Number(monster, "Count"),
                (int)Number(monster, "BehaviorTreeAI"),
                Number(monster, "Targetable", 1.0) != 0.0,
                Text(monster, "OrderSkinName"),
                Text(monster, "ChaosSkinName"),
                Text(monster, "MinimapIcon"),
                Items(monster, "Tags").Select(tag => tag.GetString() ?? "").ToList(),
                Optional(monster, "GoldAwarded"),
                Optional(monster, "ExperienceAwarded"))).ToList();
            groups.Add(new EncounterMonsterGroup(monsters));
        }
        return new EncounterDefinition(name, Number(root, "BonusValue"), Text(root, "SquadAI"), groups,
            (int)Number(root, "MaxMonstersPerSpawn"), Number(root, "SpawnDelay"));
    }

    private static MutatorDefinition ParseMutator(string name, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement.GetProperty("MutatorDefinition");
        var mutations = Items(root, "Mutations").Select(Stat).ToList();
        var graph = Items(root, "MutatedData").ToDictionary(data => (int)Number(data, "MutationIndex"),
            data => (IReadOnlyList<MutatorStat>)Items(data, "Mutation").Select(Stat).ToList());
        return new MutatorDefinition(name, Text(root, "Type"), mutations, graph);

        static MutatorStat Stat(JsonElement mutation) => new(Text(mutation, "Stat"), Number(mutation, "Value"),
            Number(mutation, "Min", double.MinValue), Number(mutation, "Max", double.MaxValue));
    }

    private static IEnumerable<JsonElement> Items(JsonElement element, string property) =>
        element.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static double? Optional(JsonElement element, string property) =>
        element.TryGetProperty(property, out _) ? Number(element, property) : null;

    private static double Number(JsonElement element, string property, double fallback = 0.0)
    {
        if (!element.TryGetProperty(property, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        throw new FormatException($"{property} is not a number");
    }
}
