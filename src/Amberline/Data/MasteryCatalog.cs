using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Amberline.Data;

/// <summary>One patch-126 mastery definition recovered from TalentTree.xml.</summary>
public sealed record MasteryDefinition(
    int Id,
    int GroupId,
    string GroupName,
    int Row,
    int Column,
    int PointsToActivate,
    int MaxRank,
    int MinLevel,
    int MinTier,
    int? PrerequisiteId,
    string DisplayName,
    IReadOnlyList<string> LevelDescriptions);

/// <summary>
/// Immutable patch-126 mastery metadata. The tree and localized text remain
/// data-driven so a launcher can expose only shipped, validated IDs.
/// </summary>
public sealed class MasteryCatalog
{
    private const string TreePath = "Talents/TalentTree.xml";
    private const string FontPath = "Menu/fontconfig_en_US.txt";
    private readonly IReadOnlyDictionary<int, MasteryDefinition> _byId;

    public GameDataPaths Paths { get; }
    public IReadOnlyList<MasteryDefinition> Definitions { get; }

    /// <summary>Metadata supplied by the authenticated host, without reading local game files.</summary>
    public MasteryCatalog(IEnumerable<MasteryDefinition> definitions)
    {
        Paths = new GameDataPaths(AppContext.BaseDirectory);
        Definitions = Array.AsReadOnly(definitions.OrderBy(definition => definition.Id).ToArray());
        _byId = Definitions.ToDictionary(definition => definition.Id);
    }

    public MasteryCatalog(GameDataPaths? paths = null)
    {
        Paths = paths ?? GameDataPaths.Default;
        var localization = LoadLocalization(Paths.Data(FontPath));
        var definitions = LoadTree(Paths.Data(TreePath), localization);
        Definitions = Array.AsReadOnly(definitions.OrderBy(definition => definition.Id).ToArray());
        _byId = Definitions.ToDictionary(definition => definition.Id);
    }

    public MasteryDefinition Get(int id) =>
        _byId.TryGetValue(id, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Unknown patch-126 mastery id {id}.");

    public bool TryGet(int id, out MasteryDefinition definition) => _byId.TryGetValue(id, out definition!);

    private static MasteryDefinition[] LoadTree(
        string path, IReadOnlyDictionary<string, string> localization)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Mastery tree was not found.", path);

        XDocument document;
        try { document = XDocument.Load(path, LoadOptions.PreserveWhitespace); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or XmlException)
        {
            throw new InvalidDataException($"Could not read mastery tree {path}.", ex);
        }

        var definitions = new List<MasteryDefinition>();
        var ids = new HashSet<int>();
        foreach (var group in document.Root?.Elements("TalentGroup")
                     ?? throw new InvalidDataException("Mastery tree has no TalentGroup root.") )
        {
            var groupId = AttributeInt(group, "id", path);
            var groupName = AttributeText(group, "name", path);
            foreach (var row in group.Elements("TalentRow"))
            {
                var rowId = AttributeInt(row, "id", path);
                var pointsToActivate = AttributeInt(row, "pointsToActivate", path);
                foreach (var talent in row.Elements("Talent"))
                {
                    var id = AttributeInt(talent, "gameCode", path);
                    if (!ids.Add(id)) throw new InvalidDataException($"Duplicate mastery id {id} in {path}.");

                    var maxRank = ElementInt(talent, "MaxRank", path);
                    var minLevel = ElementInt(talent, "MinLevel", path);
                    var minTier = ElementInt(talent, "MinTier", path);
                    var prerequisite = OptionalElementInt(talent, "PrereqTalentGameCode", path);
                    if (maxRank <= 0 || minLevel <= 0 || minTier <= 0 || pointsToActivate < 0)
                        throw new InvalidDataException($"Invalid mastery metadata for {id} in {path}.");

                    var displayKey = $"game_mastery_displayname_{id}";
                    if (!localization.TryGetValue(displayKey, out var displayName) ||
                        string.IsNullOrWhiteSpace(displayName))
                        throw new InvalidDataException($"Missing localized mastery name {displayKey}.");

                    var descriptions = new string[maxRank];
                    for (var rank = 1; rank <= maxRank; rank++)
                    {
                        var descriptionKey = $"game_mastery_level{rank}desc_{id}";
                        if (!localization.TryGetValue(descriptionKey, out var description))
                            throw new InvalidDataException($"Missing localized mastery description {descriptionKey}.");
                        descriptions[rank - 1] = description;
                    }

                    definitions.Add(new MasteryDefinition(
                        id, groupId, groupName, rowId, AttributeInt(talent, "column", path),
                        pointsToActivate, maxRank, minLevel, minTier, prerequisite,
                        displayName, Array.AsReadOnly(descriptions)));
                }
            }
        }

        return definitions.ToArray();
    }

    private static IReadOnlyDictionary<string, string> LoadLocalization(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Mastery localization was not found.", path);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("tr \"", StringComparison.Ordinal)) continue;
            var separator = line.IndexOf("\" = \"", StringComparison.Ordinal);
            if (separator < 0 || !line.EndsWith('\"')) continue;
            var key = line[4..separator];
            var value = line[(separator + 5)..^1];
            result[key] = value;
        }
        return result;
    }

    private static string AttributeText(XElement element, string name, string path) =>
        element.Attribute(name)?.Value ?? throw new InvalidDataException(
            $"Missing {name} on {element.Name} in {path}.");

    private static int AttributeInt(XElement element, string name, string path) =>
        ParseInt(AttributeText(element, name, path), $"{name} on {element.Name}", path);

    private static int ElementInt(XElement element, string name, string path) =>
        ParseInt(element.Element(name)?.Value.Trim() ?? throw new InvalidDataException(
            $"Missing {name} on {element.Name} in {path}."), $"{name} on {element.Name}", path);

    private static int? OptionalElementInt(XElement element, string name, string path)
    {
        var value = element.Element(name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : ParseInt(value, name, path);
    }

    private static int ParseInt(string value, string field, string path) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new InvalidDataException($"Invalid integer {field}='{value}' in {path}.");
}
