
namespace Amberline.Simulation.BehaviorTrees;

/// <summary>
/// The Dominion score leaves (batch 447; production table since batch 558). Like the other natives, the node is a static leaf that the runtime
/// looks up after the generic table. <see cref="DominionScore"/> sends the 0xDD meter itself, so the leaf has no work of its own.
/// </summary>
public static partial class BehaviorLeaves
{
    /// <summary>
    /// SetGameScore(Team, Score) (OdinScriptBehaviorTrees.xml:4151 and :4157). The team's total stays in the tree: the 0xDD meter is the nexus
    /// health, which <see cref="DominionScore"/> sends on every whole change (SendNexusScores). Its inputs only read, so the node succeeds.
    /// </summary>
    public static readonly Dictionary<string, BehaviorLeaf> ScoreTable = new(StringComparer.Ordinal)
    {
        ["SetGameScore"] = _ => BehaviorStatus.Success,
    };
}
