using System.Xml.Linq;
using Amberline.Core;
using Amberline.Simulation.BehaviorTrees;

namespace Amberline.Tests;

/// <summary>Batch 444: the generic leaves of the behaviour-tree runtime (variables, math, comparisons, getters).</summary>
public static class BehaviorTreeLeafChecks
{
    [Check("scripting.behaviour-tree-generic-leaves-data-and-math")]
    public static void GenericDataAndMathLeavesComputeTheShippedRelations()
    {
        var tree = Sequence(
            Node("SetVarFloat", V("Input", "2.5"), O("Output", "A")),
            Node("MultiplyFloat", R("LeftHandSide", "A"), V("RightHandSide", "4"), O("Output", "B")),
            Node("SubtractInt", V("LeftHandSide", "7"), V("RightHandSide", "9"), O("Output", "C")),
            Node("MinFloat", R("LeftHandSide", "B"), V("RightHandSide", "3"), O("Output", "D")),
            Node("MaxFloat", R("LeftHandSide", "C"), V("RightHandSide", "-1"), O("Output", "E")),
            Node("NormalizeFloatToTickRate", V("LeftHandSide", "30"), O("Output", "F")),
            Node("SetVarUnitTeam", V("Input", "TEAM_CHAOS"), O("Output", "G")),
            Node("EqualUnitTeam", R("LeftHandSide", "G"), V("RightHandSide", "TEAM_CHAOS")),
            Node("NotEqualUnitTeam", R("LeftHandSide", "G"), V("RightHandSide", "TEAM_ORDER")),
            Node("AddString", V("LeftHandSide", "Point "), R("RightHandSide", "C"), O("Output", "H")),
            Node("NotEqualString", R("LeftHandSide", "H"), V("RightHandSide", "Point 0")),
            Node("GreaterInt", R("LeftHandSide", "C"), V("RightHandSide", "-3")),
            Node("LessFloat", R("LeftHandSide", "E"), V("RightHandSide", "0")),
            Node("NotEqualInt", R("LeftHandSide", "C"), V("RightHandSide", "1")),
            Node("MakeColor", V("R", "180"), V("G", "75"), V("B", "225"), V("A", "60"), O("Color", "Col")),
            Node("MakeVector", V("X", "1"), V("Y", "2"), V("Z", "3"), O("Vector", "Vec")),
            Node("SetVarBool", V("Input", "false"), O("Output", "Flag")),
            Node("EqualBool", R("LeftHandSide", "Flag"), V("RightHandSide", "false")),
            Node("AbsFloat", R("LeftHandSide", "C"), O("Output", "Abs")),
            Node("SubtractFloat", V("LeftHandSide", "10"), V("RightHandSide", "4"), O("Output", "Sub")),
            Node("GreaterEqualInt", R("LeftHandSide", "C"), V("RightHandSide", "-2")),
            Node("DivideFloat", R("LeftHandSide", "B"), V("RightHandSide", "4"), O("Output", "Q")));
        var instance = new BehaviorTreeInstance(Library(tree), "T", new BehaviorTreeContext { World = new WorldState() });

        TestAssert.Equal(BehaviorStatus.Success, instance.Tick(0.0, 1.0 / 30.0), "every relation in the sequence holds");
        TestAssert.Near(2.5, (double)instance.Variable("A")!, 1e-9, "SetVarFloat keeps the value");
        TestAssert.Near(10.0, (double)instance.Variable("B")!, 1e-9, "MultiplyFloat");
        TestAssert.Near(-2.0, (double)instance.Variable("C")!, 1e-9, "SubtractInt");
        TestAssert.Near(3.0, (double)instance.Variable("D")!, 1e-9, "MinFloat takes the smaller");
        TestAssert.Near(-1.0, (double)instance.Variable("E")!, 1e-9, "MaxFloat takes the larger");
        // Batch 450: the node divides the step by the 0.1 s reference tick (BehaviorLeaves.ReferenceTickSeconds), so one 1/30 s step of 30 is 10
        // (it was 1 when the node multiplied by the step alone).
        TestAssert.Near(10.0, (double)instance.Variable("F")!, 1e-9, "NormalizeFloatToTickRate: value times the step over the 0.1 s reference tick");
        TestAssert.Equal(Team.Chaos, (Team)instance.Variable("G")!, "SetVarUnitTeam keeps the team");
        TestAssert.Equal("Point -2", (string)instance.Variable("H")!, "AddString joins the text of a number");
        TestAssert.True(instance.Variable("Col") is BehaviorColor { R: 180, G: 75, B: 225, A: 60 }, "MakeColor builds its colour");
        TestAssert.Equal(new Position3(1, 2, 3), (Position3)instance.Variable("Vec")!, "MakeVector builds its point");
        TestAssert.Near(2.5, (double)instance.Variable("Q")!, 1e-9, "DivideFloat divides by four");
        TestAssert.Near(2.0, (double)instance.Variable("Abs")!, 1e-9, "AbsFloat of -2");
        TestAssert.Near(6.0, (double)instance.Variable("Sub")!, 1e-9, "SubtractFloat");

        var refused = Selector(
            Node("DivideFloat", V("LeftHandSide", "1"), V("RightHandSide", "0"), O("Output", "Zero")),
            Node("EqualInt", V("LeftHandSide", "1"), V("RightHandSide", "2")),
            Node("GreaterInt", V("LeftHandSide", "1"), V("RightHandSide", "2")));
        var none = new BehaviorTreeInstance(Library(refused), "T", new BehaviorTreeContext { World = new WorldState() });
        TestAssert.Equal(BehaviorStatus.Failure, none.Tick(0.0, 1.0 / 30.0), "a division by zero and two false relations all fail");
        TestAssert.True(none.Variable("Zero") is null, "a refused division writes nothing");
    }

    [Check("scripting.behaviour-tree-generic-leaves-world-getters")]
    public static void WorldGettersReadTheWorldAndTheMapNames()
    {
        var world = new WorldState();
        var hero = world.Register(new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero, Team.Chaos,
            new Position2(1200, 3400)));
        var turret = world.Register(new Entity(world.Allocate(UnitType.Turret), EntityKind.Turret, UnitType.Turret, Team.Order,
            new Position2(10, 20)));
        var named = new Dictionary<string, Position3> { ["OrderStartPoint"] = new(100, 5, 200) };
        var reports = new List<string>();
        var context = new BehaviorTreeContext
        {
            World = world,
            NamedLocation = name => named.TryGetValue(name, out var point) ? (Position3?)point : null,
            Height = _ => 7.5,
            Turret = (team, _, position) => team == Team.Order && position == 2 ? turret : null,
            Sink = (key, _) => reports.Add(key),
        };
        var tree = Sequence(
            Node("GetWorldLocationByName", V("Input", "OrderStartPoint"), O("Output", "Start")),
            Node("GetWorldLocationByName", V("Input", "GraveyardA"), O("Output", "Grave")),
            Node("GetUnitTeam", R("Unit", "Hero"), O("Output", "Team")),
            Node("GetUnitPosition", R("Unit", "Hero"), O("Output", "Pos")),
            Node("GetChampionCollection", O("Output", "Heroes")),
            Node("GetCollectionCount", R("Collection", "Heroes"), O("Output", "Count")),
            Node("GetGameTime", O("Output", "Time")),
            Node("GetTurret", V("Team", "TEAM_ORDER"), V("Lane", "0"), V("Position", "2"), O("Turret", "Own")),
            Node("GetTurret", V("Team", "TEAM_CHAOS"), V("Lane", "0"), V("Position", "6"), O("Turret", "Missing")));
        var instance = new BehaviorTreeInstance(Library(tree), "T", context);
        instance.SetVariable("Hero", hero);

        TestAssert.Equal(BehaviorStatus.Success, instance.Tick(12.5, 1.0 / 30.0), "the getters succeed on a map that names them");
        TestAssert.Equal(new Position3(100, 5, 200), (Position3)instance.Variable("Start")!, "a named location is its mesh's point");
        TestAssert.True(instance.Variable("Grave") is null, "an unnamed location leaves its output unset");
        TestAssert.True(reports.Contains("location:GraveyardA"), "the unnamed location is reported");
        TestAssert.Equal(Team.Chaos, (Team)instance.Variable("Team")!, "GetUnitTeam reads the unit's team");
        TestAssert.Equal(new Position3(1200, 7.5, 3400), (Position3)instance.Variable("Pos")!,
            "GetUnitPosition uses the unit's ground height");
        TestAssert.Near(1.0, (double)instance.Variable("Count")!, 1e-9, "GetCollectionCount counts the heroes");
        TestAssert.Equal(1, ((IReadOnlyList<Entity>)instance.Variable("Heroes")!).Count, "GetChampionCollection lists the heroes");
        TestAssert.Near(12.5, (double)instance.Variable("Time")!, 1e-9, "GetGameTime is the tick's time");
        TestAssert.True(ReferenceEquals(instance.Variable("Own"), turret), "GetTurret answers the slot the map names");
        TestAssert.True(instance.Variable("Missing") is null && reports.Contains("turret:Chaos:0:6"),
            "an unnamed turret slot answers nil and is reported");

        var dead = Selector(
            Node("GetUnitPosition", R("Unit", "Nobody"), O("Output", "Lost")),
            Node("GetUnitTeam", R("Unit", "Nobody"), O("Output", "LostTeam")));
        var nobody = new BehaviorTreeInstance(Library(dead), "T", context);
        TestAssert.Equal(BehaviorStatus.Failure, nobody.Tick(0.0, 1.0 / 30.0), "a missing unit fails its getters");
    }

    /// <summary>
    /// SetUnitNotTargetableToTeam replaces the unit's flags (the native resets the flags word and then
    /// writes the new value): a turret set for one team and then the other carries only the second, as after a neutralise and a recapture.
    /// </summary>
    [Check("scripting.behaviour-tree-not-targetable-to-team-replaces-the-flags")]
    public static void NotTargetableToTeamReplacesTheFlags()
    {
        var world = new WorldState();
        var turret = world.Register(new Entity(world.Allocate(UnitType.Turret), EntityKind.Turret, UnitType.Turret, Team.Order,
            new Position2(1000, 1000)) { Name = "OdinOrderTurretShrine", ChampionName = "OdinOrderTurretShrine" });
        var tree = Sequence(
            Node("SetUnitNotTargetableToTeam", R("Unit", "U"), V("Team", "TEAM_ORDER")),
            Node("SetUnitNotTargetableToTeam", R("Unit", "U"), V("Team", "TEAM_CHAOS")));
        var instance = new BehaviorTreeInstance(Library(tree), "T", new BehaviorTreeContext { World = world });
        instance.SetVariable("U", turret);
        TestAssert.Equal(BehaviorStatus.Success, instance.Tick(0.0, 1.0 / 30.0), "both nodes run");
        TestAssert.True(!turret.NotTargetableToAlly && turret.NotTargetableToEnemy,
            $"only the second call's flag stands (ally {turret.NotTargetableToAlly}, enemy {turret.NotTargetableToEnemy})");
    }

    private static string Node(string type, params string[] parameters) =>
        $"<Node Type=\"{type}\" Name=\"{type}\"><Parameters>{string.Concat(parameters)}</Parameters></Node>";

    private static string Sequence(params string[] nodes) =>
        $"<Node Type=\"Sequence\" Name=\"Root\"><Parameters /><Children>{string.Concat(nodes)}</Children></Node>";

    private static string Selector(params string[] nodes) =>
        $"<Node Type=\"Selector\" Name=\"Root\"><Parameters /><Children>{string.Concat(nodes)}</Children></Node>";

    private static string V(string name, string value) => $"<Parameter Name=\"{name}\" Value=\"{value}\" VariableType=\"Value\" />";

    private static string R(string name, string variable) => $"<Parameter Name=\"{name}\" Value=\"{variable}\" VariableType=\"Reference\" />";

    private static string O(string name, string variable) => $"<OutParameter Name=\"{name}\" Value=\"{variable}\" />";

    private static BehaviorTreeLibrary Library(string body)
    {
        var library = BehaviorTreeLoader.Parse([("leaf-test.xml", XDocument.Parse(
            $"<BehaviorTrees><BehaviorTree Name=\"T\"><Parameters />{body}</BehaviorTree></BehaviorTrees>"))]);
        TestAssert.Equal(0, library.Errors.Count, "the in-test tree is well formed");
        return library;
    }
}
