using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;

namespace Amberline.Data;

public enum ClassicHeroStatKind
{
    Int,
    Float,
    String,
    LongLong,
}

public sealed record ClassicHeroStatDefinition(string Name, ClassicHeroStatKind Kind)
{
    public string Key => "HeroStats*" + Name;
}

/// <summary>
/// Reads the exact typed HeroStats collection of a game mode. The client builds it from <c>Data\Globals\&lt;mode&gt;_Stats.ini</c> with the
/// mode string SynchVersion carried (the client's string table gives "Data\Globals\" + mode + "_Stats.ini"; the mode is
/// copied from the SynchVersion record), so the 0x4B must follow the same mode: CLASSIC (58 fields) on Summoner's Rift, ODIN (61) on map 8.
/// </summary>
public sealed class ClassicHeroStatsSchema
{
    /// <summary>The CLASSIC collection's field count.</summary>
    public const int FieldCount = 58;
    private const string StatListPath = "Globals/HeroStatList.txt";

    private ClassicHeroStatsSchema(IReadOnlyList<ClassicHeroStatDefinition> definitions) =>
        Definitions = definitions;

    public IReadOnlyList<ClassicHeroStatDefinition> Definitions { get; }

    public static ClassicHeroStatsSchema Load(GameDataPaths paths, string mode = Amberline.Core.GameMaps.ClassicMode)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var names = File.ReadAllLines(paths.Data(StatListPath), Encoding.ASCII)
            .Select(line => line.Trim())
            .Where(line => line.Length != 0 && !line.StartsWith('#'))
            .ToArray();
        var nameSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!nameSet.Add(name))
                throw new InvalidDataException($"duplicate HeroStats name {name}");
        }

        var types = InibinReader.Load(paths, $"Globals/{mode}_Stats.inibin", raw: true);
        var definitions = new List<ClassicHeroStatDefinition>(names.Length);
        var matched = new HashSet<uint>();
        foreach (var name in names)
        {
            var keyHash = InibinReader.KeyHash("HeroStats*" + name);
            if (!types.TryGetValue(keyHash, out var typeValue)) continue;
            if (typeValue.Kind != InibinValueKind.String)
                throw new InvalidDataException($"{name} type is {typeValue.Kind}, expected a type string");
            if (!matched.Add(keyHash))
                throw new InvalidDataException($"duplicate HeroStats hash for {name}");
            definitions.Add(new ClassicHeroStatDefinition(name, ParseKind(name, typeValue.AsString())));
        }

        if (definitions.Count != types.Count)
            throw new InvalidDataException($"{mode} HeroStats matched {definitions.Count} of {types.Count} mode entries");

        definitions.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        return new ClassicHeroStatsSchema(new ReadOnlyCollection<ClassicHeroStatDefinition>(definitions));
    }

    private static ClassicHeroStatKind ParseKind(string name, string type) => type switch
    {
        "INT" => ClassicHeroStatKind.Int,
        "FLOAT" => ClassicHeroStatKind.Float,
        "STRING" => ClassicHeroStatKind.String,
        "LONGLONG" => ClassicHeroStatKind.LongLong,
        _ => throw new InvalidDataException($"{name} has unsupported Classic HeroStats type {type}")
    };
}
