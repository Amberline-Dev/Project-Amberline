using Amberline.Core;

namespace Amberline.Simulation.BehaviorTrees;

/// <summary>What one tick of a node reports. <see cref="BehaviorStatus.Running"/> resumes at the same child next tick.</summary>
public enum BehaviorStatus
{
    Failure = 0,
    Success = 1,
    Running = 2,
}

/// <summary>
/// One input of a node. <c>VariableType="Value"</c> is a literal, typed by <see cref="Literal"/> (a number, a boolean, a
/// <see cref="Team"/> for TEAM_* or the text); <c>"Reference"</c> names a variable (<see cref="Text"/>) and has no literal;
/// <c>"Reference, Value"</c> (tree inputs only) is the variable when it is set, else the literal.
/// </summary>
public sealed record BehaviorBinding(string Name, string VariableType, string Text, string? DeclaredType, object? Literal);

/// <summary>An OutParameter of a node: the output <see cref="Name"/> is written to the variable <see cref="Variable"/>.</summary>
public sealed record BehaviorOutput(string Name, string Variable, string? DeclaredType);

/// <summary>A tree-level Parameter or OutParameter: the interface a caller binds (<c>Procedure</c>) or reads back.</summary>
public sealed record BehaviorParameter(string Name, string VariableType, string ParameterType, string Default, bool Optional);

/// <summary>One node of a tree: its type, its instance name, its bindings and its children in file order.</summary>
public sealed class BehaviorNode
{
    public BehaviorNode(string type, string name)
    {
        Type = type;
        Name = name;
    }

    public string Type { get; }
    public string Name { get; }
    public List<BehaviorBinding> Inputs { get; } = new();
    public List<BehaviorOutput> Outputs { get; } = new();
    public List<BehaviorNode> Children { get; } = new();

    /// <summary>The tree a Procedure runs: the node's Name is the callee's tree name (phase 0, section 5).</summary>
    public string? ProcedureTarget => Type == "Procedure" ? Name : null;

    public BehaviorBinding? Input(string name) => Inputs.FirstOrDefault(binding => binding.Name == name);

    public BehaviorOutput? Output(string name) => Outputs.FirstOrDefault(output => output.Name == name);
}

/// <summary>One tree: its interface (inputs and outputs), its root node and the shipped file it came from.</summary>
public sealed class BehaviorTreeDefinition
{
    public BehaviorTreeDefinition(string name, string source)
    {
        Name = name;
        Source = source;
    }

    public string Name { get; }

    /// <summary>The shipped file the tree was read from, e.g. <c>OdinScriptBehaviorTrees.xml</c>.</summary>
    public string Source { get; }

    public List<BehaviorParameter> Inputs { get; } = new();
    public List<BehaviorParameter> Outputs { get; } = new();
    public BehaviorNode? Root { get; set; }

    /// <summary>The nodes of the tree, the root included.</summary>
    public int NodeCount { get; set; }
}

/// <summary>One quest instance of a map's <c>OdinScript.xml</c>: the tree it runs and whether the file enables it.</summary>
public sealed record BehaviorQuestInstance(string Name, string Type, string TreeName, bool Enabled);

/// <summary>A parsed set of trees: the trees by name, the quest instances, the structural errors and the node-type census.</summary>
public sealed class BehaviorTreeLibrary
{
    public BehaviorTreeLibrary(IReadOnlyDictionary<string, BehaviorTreeDefinition> trees,
        IReadOnlyList<BehaviorQuestInstance> questInstances, IReadOnlyList<string> errors,
        IReadOnlyDictionary<string, int> nodeTypeCensus)
    {
        Trees = trees;
        QuestInstances = questInstances;
        Errors = errors;
        NodeTypeCensus = nodeTypeCensus;
    }

    public IReadOnlyDictionary<string, BehaviorTreeDefinition> Trees { get; }
    public IReadOnlyList<BehaviorQuestInstance> QuestInstances { get; }

    /// <summary>Structural problems (never thrown): an unnamed or doubled tree, a node without a Type, a Procedure to an unknown tree.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Occurrences of every node type across the loaded trees.</summary>
    public IReadOnlyDictionary<string, int> NodeTypeCensus { get; }

    public int NodeCount => Trees.Values.Sum(tree => tree.NodeCount);
}
