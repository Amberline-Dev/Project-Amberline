using System.Xml.Linq;
using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.BehaviorTrees;

namespace Amberline.Tests;

/// <summary>Batch 444: the Map8 behaviour-tree loader (the shipped quest and tower trees, parsed generically).</summary>
public static class BehaviorTreeChecks
{
    [Check("scripting.behaviour-tree-loader-parses-all-37-shipped-map8-trees")]
    public static void LoaderParsesEveryShippedMap8Tree()
    {
        var library = BehaviorTreeLoader.LoadMap(new GameDataPaths(null, "Map8"));
        TestAssert.Equal(0, library.Errors.Count, "every shipped Map8 tree loads with zero structural errors");
        TestAssert.Equal(37, library.Trees.Count, "18 quest trees and 19 tower trees");
        TestAssert.Equal(1149, library.NodeCount, "874 quest nodes and 275 tower nodes (phase 0, section 5)");
        // The phase-0 note said 111; the shipped files hold 116 distinct types (76 quest, 58 tower, 18 shared), re-counted with rg.
        TestAssert.Equal(116, library.NodeTypeCensus.Count, "116 distinct node types across both files");
        TestAssert.True(library.Trees.ContainsKey("CapturePointManager") && library.Trees.ContainsKey("LocationIllumination"),
            "the live quest tree and its procedure are loaded");
    }

    [Check("scripting.behaviour-tree-quest-instances-match-odin-script")]
    public static void QuestInstancesMatchTheShippedOdinScript()
    {
        var library = BehaviorTreeLoader.LoadMap(new GameDataPaths(null, "Map8"));
        TestAssert.Equal(3, library.QuestInstances.Count, "OdinScript.xml has three quest instances");
        var quests = library.QuestInstances.ToDictionary(quest => quest.Name);
        TestAssert.True(quests["CapturePointManager"].Enabled, "CapturePointManager is enabled");
        TestAssert.False(quests["ScoreboardPrototypeManager"].Enabled, "ScoreboardPrototypeManager is disabled");
        TestAssert.True(quests["OdinOpeningCeremony"].Enabled, "OdinOpeningCeremony is enabled");
        TestAssert.Equal("CapturePointManager", quests["CapturePointManager"].TreeName, "each instance names its tree");
    }

    [Check("scripting.behaviour-tree-loader-reports-structural-errors-without-throwing")]
    public static void LoaderReportsBrokenTreesAsErrors()
    {
        const string xml = """
            <BehaviorTrees>
              <BehaviorTree Name="Root">
                <Parameters />
                <Node Type="Sequence" Name="Sequence">
                  <Parameters />
                  <Children>
                    <Node Type="Procedure" Name="NoSuchTree"><Parameters /></Node>
                    <Node Type="MaskFailure" Name="Masked"><Parameters /></Node>
                  </Children>
                </Node>
              </BehaviorTree>
              <BehaviorTree Name="Root">
                <Parameters />
                <Node Type="Sequence" Name="Sequence"><Parameters /></Node>
              </BehaviorTree>
            </BehaviorTrees>
            """;
        var library = BehaviorTreeLoader.Parse([("test.xml", XDocument.Parse(xml))]);
        TestAssert.Equal(3, library.Errors.Count, "a doubled tree, an unknown procedure target and a decorator without its child");
        TestAssert.True(library.Errors.Any(error => error.Contains("NoSuchTree")), "the unknown procedure target is named");
        TestAssert.True(library.Errors.Any(error => error.Contains("defined twice")), "the doubled tree is named");
        TestAssert.True(library.Errors.Any(error => error.Contains("MaskFailure")), "the childless decorator is named");
    }

    [Check("scripting.behaviour-tree-literals-keep-their-shipped-types")]
    public static void LiteralsKeepTheirShippedTypes()
    {
        TestAssert.Near(0.14, (double)BehaviorTreeLoader.ParseLiteral("0.14")!, 1e-12, "a decimal literal is a number");
        TestAssert.Near(-1.0, (double)BehaviorTreeLoader.ParseLiteral("-1")!, 1e-12, "a negative literal is a number");
        TestAssert.True((bool)BehaviorTreeLoader.ParseLiteral("true")! && !(bool)BehaviorTreeLoader.ParseLiteral("False")!,
            "true and false are booleans");
        TestAssert.Equal(Team.Chaos, (Team)BehaviorTreeLoader.ParseLiteral("TEAM_CHAOS")!, "TEAM_CHAOS is a team");
        TestAssert.Equal("OrderStartPoint", (string)BehaviorTreeLoader.ParseLiteral("OrderStartPoint")!, "a name stays text");
        TestAssert.True(BehaviorTreeLoader.ParseLiteral("") is null, "empty text has no value");
    }
}
