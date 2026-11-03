using System.Globalization;
using System.Xml.Linq;
using Amberline.Core;
using Amberline.Data;

namespace Amberline.Simulation.BehaviorTrees;

/// <summary>
/// Reads the shipped behaviour trees generically: each tree's interface, node shape, bindings and procedure references, and
/// the quest instances of <c>OdinScript.xml</c>. No node meaning is applied here; the runtime and its leaves do that. Structural
/// problems go to <see cref="BehaviorTreeLibrary.Errors"/> instead of throwing, so a map reports everything wrong with it at once.
/// </summary>
public static class BehaviorTreeLoader
{
    /// <summary>The tree files of a map's <c>Scripts</c> folder: the quest trees and the Odin tower AI trees.</summary>
    public static readonly string[] TreeFiles = ["OdinScriptBehaviorTrees.xml", "OdinTowerBehaviorTrees.xml"];

    /// <summary>The quest instance file of a map's <c>Scripts</c> folder.</summary>
    public const string QuestFile = "OdinScript.xml";

    /// <summary>Loads the trees and the quest instances of one map (<c>LEVELS/&lt;MapName&gt;/Scripts</c>).</summary>
    public static BehaviorTreeLibrary LoadMap(GameDataPaths paths)
    {
        var trees = TreeFiles.Select(file => (Source: file, Document: XDocument.Load(paths.Under("LEVELS", paths.MapName, "Scripts", file)))).ToList();
        return Parse(trees, XDocument.Load(paths.Under("LEVELS", paths.MapName, "Scripts", QuestFile)));
    }

    /// <summary>Parses tree documents (with their source names) and an optional quest document.</summary>
    public static BehaviorTreeLibrary Parse(IEnumerable<(string Source, XDocument Document)> treeDocuments, XDocument? questDocument = null)
    {
        var errors = new List<string>();
        var trees = new Dictionary<string, BehaviorTreeDefinition>(StringComparer.Ordinal);
        var census = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (source, document) in treeDocuments)
            foreach (var element in document.Root?.Elements("BehaviorTree") ?? Enumerable.Empty<XElement>())
                ReadTree(element, source, trees, census, errors);
        foreach (var tree in trees.Values)
            if (tree.Root is not null) CheckNode(tree.Root, tree, trees, errors);
        var quests = questDocument is null ? new List<BehaviorQuestInstance>() : ReadQuests(questDocument, trees, errors);
        return new BehaviorTreeLibrary(trees, quests, errors, census);
    }

    /// <summary>The typed value of a literal: a number, a boolean, a team (TEAM_*) or the text itself. Empty text has no value.</summary>
    public static object? ParseLiteral(string text)
    {
        if (text.Length == 0) return null;
        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) return false;
        switch (text)
        {
            case "TEAM_ORDER": return Team.Order;
            case "TEAM_CHAOS": return Team.Chaos;
            case "TEAM_NEUTRAL": return Team.Neutral;
        }
        if ((char.IsDigit(text[0]) || text[0] is '-' or '+' or '.')
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return number;
        return text;
    }

    private static void ReadTree(XElement element, string source, Dictionary<string, BehaviorTreeDefinition> trees,
        Dictionary<string, int> census, List<string> errors)
    {
        var name = element.Attribute("Name")?.Value ?? "";
        if (name.Length == 0)
        {
            errors.Add($"{source}: a BehaviorTree without a Name");
            return;
        }
        if (trees.ContainsKey(name))
        {
            errors.Add($"{source}: tree {name} is defined twice");
            return;
        }
        var tree = new BehaviorTreeDefinition(name, source);
        trees.Add(name, tree);
        foreach (var child in element.Element("Parameters")?.Elements() ?? Enumerable.Empty<XElement>())
        {
            var parameter = new BehaviorParameter(child.Attribute("Name")?.Value ?? "",
                child.Attribute("VariableType")?.Value ?? "", child.Attribute("ParameterType")?.Value ?? "",
                child.Attribute("Default")?.Value ?? "", string.Equals(child.Attribute("Optional")?.Value, "true", StringComparison.OrdinalIgnoreCase));
            if (parameter.Name.Length == 0) errors.Add($"{source}: an unnamed parameter in {name}");
            switch (child.Name.LocalName)
            {
                case "Parameter": tree.Inputs.Add(parameter); break;
                case "OutParameter": tree.Outputs.Add(parameter); break;
                default: errors.Add($"{source}: unknown tag {child.Name.LocalName} in the parameters of {name}"); break;
            }
        }
        var bodies = element.Elements("Node").ToList();
        if (bodies.Count != 1)
            errors.Add($"{source}: tree {name} has {bodies.Count} root nodes, expected one");
        else
            tree.Root = ReadNode(bodies[0], tree, source, census, errors);
    }

    private static BehaviorNode ReadNode(XElement element, BehaviorTreeDefinition tree, string source,
        Dictionary<string, int> census, List<string> errors)
    {
        var type = element.Attribute("Type")?.Value ?? "";
        if (type.Length == 0) errors.Add($"{source}: a node in {tree.Name} has no Type");
        var node = new BehaviorNode(type, element.Attribute("Name")?.Value ?? "");
        tree.NodeCount++;
        census[type] = census.GetValueOrDefault(type) + 1;
        foreach (var child in element.Element("Parameters")?.Elements() ?? Enumerable.Empty<XElement>())
        {
            var name = child.Attribute("Name")?.Value ?? "";
            var value = child.Attribute("Value")?.Value ?? "";
            var declared = child.Attribute("ReferenceType")?.Value;
            if (name.Length == 0)
            {
                errors.Add($"{source}: an unnamed parameter on {type} in {tree.Name}");
                continue;
            }
            switch (child.Name.LocalName)
            {
                case "Parameter":
                    // Every shipped binding carries a VariableType; a missing one reads as a literal (Reconstruction).
                    var variableType = child.Attribute("VariableType")?.Value ?? "Value";
                    // NOT RETAIL: the opening ceremony releases at 80 s, not the shipped 45 (OpeningCeremonyTiming).
                    var bound = OpeningCeremonyTiming.ReleaseOverride(source, tree.Name, type, name, value);
                    node.Inputs.Add(new BehaviorBinding(name, variableType, bound, declared,
                        variableType == "Reference" ? null : ParseLiteral(bound)));
                    break;
                case "OutParameter":
                    node.Outputs.Add(new BehaviorOutput(name, value, declared));
                    break;
                default:
                    errors.Add($"{source}: unknown tag {child.Name.LocalName} on {type} in {tree.Name}");
                    break;
            }
        }
        foreach (var child in element.Element("Children")?.Elements() ?? Enumerable.Empty<XElement>())
        {
            if (child.Name.LocalName == "Node")
                node.Children.Add(ReadNode(child, tree, source, census, errors));
            else
                errors.Add($"{source}: unknown tag {child.Name.LocalName} under {type} in {tree.Name}");
        }
        return node;
    }

    private static void CheckNode(BehaviorNode node, BehaviorTreeDefinition tree,
        Dictionary<string, BehaviorTreeDefinition> trees, List<string> errors)
    {
        if (node.ProcedureTarget is { } target && !trees.ContainsKey(target))
            errors.Add($"{tree.Source}: {tree.Name} calls the unknown tree {target}");
        // Reconstruction: a decorator wraps exactly one child; the shipped files never use another arity.
        if ((node.Type is "IterateOverAllDecorator" or "MaskFailure") && node.Children.Count != 1)
            errors.Add($"{tree.Source}: decorator {node.Type} in {tree.Name} has {node.Children.Count} children, expected one");
        foreach (var child in node.Children)
            CheckNode(child, tree, trees, errors);
    }

    private static List<BehaviorQuestInstance> ReadQuests(XDocument document, Dictionary<string, BehaviorTreeDefinition> trees,
        List<string> errors)
    {
        var quests = new List<BehaviorQuestInstance>();
        foreach (var instance in document.Root?.Elements("BTInstance") ?? Enumerable.Empty<XElement>())
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var parameter in instance.Element("BehaviorTreeParameters")?.Elements("Parameter") ?? Enumerable.Empty<XElement>())
                values[parameter.Attribute("Name")?.Value ?? ""] = parameter.Attribute("Value")?.Value ?? "";
            var name = instance.Attribute("Name")?.Value ?? "";
            var tree = values.GetValueOrDefault("BehaviorTree") ?? "";
            if (!trees.ContainsKey(tree)) errors.Add($"{QuestFile}: quest {name} runs the unknown tree {tree}");
            quests.Add(new BehaviorQuestInstance(name, instance.Attribute("Type")?.Value ?? "", tree,
                string.Equals(values.GetValueOrDefault("Enabled"), "True", StringComparison.OrdinalIgnoreCase)));
        }
        return quests;
    }
}
