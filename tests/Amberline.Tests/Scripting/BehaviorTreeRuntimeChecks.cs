using System.Xml.Linq;
using Amberline.Core;
using Amberline.Simulation.BehaviorTrees;

namespace Amberline.Tests;

/// <summary>Batch 444: the behaviour-tree runtime (composites, decorators, procedure calls, running state, unknown leaves).</summary>
public static class BehaviorTreeRuntimeChecks
{
    private const string CompositeXml = """
        <BehaviorTrees>
          <BehaviorTree Name="Main">
            <Parameters />
            <Node Type="Sequence" Name="Root">
              <Parameters />
              <Children>
                <Node Type="SetVarInt" Name="start">
                  <Parameters>
                    <Parameter Name="Input" Value="0" VariableType="Value" />
                    <OutParameter Name="Output" Value="Total" ReferenceType="Int" />
                  </Parameters>
                </Node>
                <Node Type="IterateOverAllDecorator" Name="count">
                  <Parameters>
                    <Parameter Name="Collection" Value="Items" VariableType="Reference" ReferenceType="AttackableUnitCollection" />
                    <OutParameter Name="Output" Value="Item" ReferenceType="AttackableUnit" />
                  </Parameters>
                  <Children>
                    <Node Type="AddInt" Name="add">
                      <Parameters>
                        <Parameter Name="LeftHandSide" Value="Total" VariableType="Reference" ReferenceType="Int" />
                        <Parameter Name="RightHandSide" Value="1" VariableType="Value" />
                        <OutParameter Name="Output" Value="Total" ReferenceType="Int" />
                      </Parameters>
                    </Node>
                  </Children>
                </Node>
                <Node Type="Selector" Name="pick">
                  <Parameters />
                  <Children>
                    <Node Type="Sequence" Name="wrong">
                      <Parameters />
                      <Children>
                        <Node Type="SetVarInt" Name="mark wrong">
                          <Parameters>
                            <Parameter Name="Input" Value="1" VariableType="Value" />
                            <OutParameter Name="Output" Value="Wrong" ReferenceType="Int" />
                          </Parameters>
                        </Node>
                        <Node Type="EqualInt" Name="total is 99">
                          <Parameters>
                            <Parameter Name="LeftHandSide" Value="Total" VariableType="Reference" ReferenceType="Int" />
                            <Parameter Name="RightHandSide" Value="99" VariableType="Value" />
                          </Parameters>
                        </Node>
                      </Children>
                    </Node>
                    <Node Type="Sequence" Name="right">
                      <Parameters />
                      <Children>
                        <Node Type="SetVarInt" Name="mark right">
                          <Parameters>
                            <Parameter Name="Input" Value="1" VariableType="Value" />
                            <OutParameter Name="Output" Value="Right" ReferenceType="Int" />
                          </Parameters>
                        </Node>
                        <Node Type="EqualInt" Name="total is 3">
                          <Parameters>
                            <Parameter Name="LeftHandSide" Value="Total" VariableType="Reference" ReferenceType="Int" />
                            <Parameter Name="RightHandSide" Value="3" VariableType="Value" />
                          </Parameters>
                        </Node>
                      </Children>
                    </Node>
                  </Children>
                </Node>
                <Node Type="MaskFailure" Name="masked">
                  <Parameters />
                  <Children>
                    <Node Type="EqualInt" Name="fails">
                      <Parameters>
                        <Parameter Name="LeftHandSide" Value="Total" VariableType="Reference" ReferenceType="Int" />
                        <Parameter Name="RightHandSide" Value="99" VariableType="Value" />
                      </Parameters>
                    </Node>
                  </Children>
                </Node>
                <Node Type="Procedure" Name="Double">
                  <Parameters>
                    <Parameter Name="Seed" Value="Total" VariableType="Reference" ReferenceType="Int" />
                    <OutParameter Name="Result" Value="Doubled" ReferenceType="Int" />
                  </Parameters>
                </Node>
              </Children>
            </Node>
          </BehaviorTree>
          <BehaviorTree Name="Double">
            <Parameters>
              <Parameter Name="Seed" VariableType="Reference" ParameterType="Int" Default="" Optional="false" />
              <OutParameter Name="Result" ParameterType="Int" />
            </Parameters>
            <Node Type="AddInt" Name="twice">
              <Parameters>
                <Parameter Name="LeftHandSide" Value="Seed" VariableType="Reference" ReferenceType="Int" />
                <Parameter Name="RightHandSide" Value="Seed" VariableType="Reference" ReferenceType="Int" />
                <OutParameter Name="Output" Value="Result" ReferenceType="Int" />
              </Parameters>
            </Node>
          </BehaviorTree>
        </BehaviorTrees>
        """;

    private const string WaitingXml = """
        <BehaviorTrees>
          <BehaviorTree Name="Waiting">
            <Parameters />
            <Node Type="Sequence" Name="Root">
              <Parameters />
              <Children>
                <Node Type="AddInt" Name="count once">
                  <Parameters>
                    <Parameter Name="LeftHandSide" Value="Count" VariableType="Reference" ReferenceType="Int" />
                    <Parameter Name="RightHandSide" Value="1" VariableType="Value" />
                    <OutParameter Name="Output" Value="Count" ReferenceType="Int" />
                  </Parameters>
                </Node>
                <Node Type="Wait" Name="wait">
                  <Parameters />
                </Node>
                <Node Type="SetVarInt" Name="after">
                  <Parameters>
                    <Parameter Name="Input" Value="1" VariableType="Value" />
                    <OutParameter Name="Output" Value="After" ReferenceType="Int" />
                  </Parameters>
                </Node>
              </Children>
            </Node>
          </BehaviorTree>
          <BehaviorTree Name="FirstTick">
            <Parameters />
            <Node Type="Selector" Name="Root">
              <Parameters />
              <Children>
                <Node Type="EqualBool" Name="first">
                  <Parameters>
                    <Parameter Name="LeftHandSide" Value="__IsFirstRun" VariableType="Reference" ReferenceType="Bool" />
                    <Parameter Name="RightHandSide" Value="true" VariableType="Value" />
                  </Parameters>
                </Node>
              </Children>
            </Node>
          </BehaviorTree>
          <BehaviorTree Name="Unknown">
            <Parameters />
            <Node Type="Selector" Name="Root">
              <Parameters />
              <Children>
                <Node Type="NoSuchGameAction" Name="act">
                  <Parameters />
                </Node>
              </Children>
            </Node>
          </BehaviorTree>
        </BehaviorTrees>
        """;

    [Check("scripting.behaviour-tree-runtime-composites-decorators-and-procedures")]
    public static void CompositesDecoratorsAndProceduresRunAsTheShippedShape()
    {
        var library = Library(CompositeXml);
        var instance = new BehaviorTreeInstance(library, "Main", new BehaviorTreeContext { World = new WorldState() });
        instance.SetVariable("Items", new List<Entity>
        {
            new(1, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(0, 0)),
            new(2, EntityKind.Hero, UnitType.Hero, Team.Chaos, new Position2(0, 0)),
            new(3, EntityKind.Minion, UnitType.Minion, Team.Order, new Position2(0, 0)),
        });

        TestAssert.Equal(BehaviorStatus.Success, instance.Tick(0.0, 1.0 / 30.0), "the root sequence succeeds");
        TestAssert.Near(3.0, (double)instance.Variable("Total")!, 1e-9, "the decorator runs its child once per element");
        TestAssert.True(instance.Variable("Item") is Entity { NetId: 3 }, "the loop variable ends on the last element");
        TestAssert.Near(1.0, (double)instance.Variable("Wrong")!, 1e-9, "the first branch ran its write, then its failing test ended it");
        TestAssert.Near(1.0, (double)instance.Variable("Right")!, 1e-9, "the selector falls through to the matching branch");
        TestAssert.Near(6.0, (double)instance.Variable("Doubled")!, 1e-9, "the procedure's output is copied back to the caller");
        TestAssert.True(instance.Variable("Seed") is null, "the callee's inputs stay in the callee's frame");
    }

    [Check("scripting.behaviour-tree-runtime-running-resumes-and-first-tick-flag")]
    public static void RunningResumesAtTheWaitingChildAndTheFirstTickFlagIsSet()
    {
        var waits = 0;
        var context = new BehaviorTreeContext { World = new WorldState() };
        context.Leaves["Wait"] = _ => ++waits <= 2 ? BehaviorStatus.Running : BehaviorStatus.Success;
        var library = Library(WaitingXml);

        var waiting = new BehaviorTreeInstance(library, "Waiting", context);
        TestAssert.Equal(BehaviorStatus.Running, waiting.Tick(0.0, 1.0 / 30.0), "the first wait reports running");
        TestAssert.Equal(BehaviorStatus.Running, waiting.Tick(1.0 / 30.0, 1.0 / 30.0), "the second wait reports running");
        TestAssert.True(waiting.Variable("After") is null, "the sequence does not pass a running child");
        TestAssert.Equal(BehaviorStatus.Success, waiting.Tick(2.0 / 30.0, 1.0 / 30.0), "the third tick succeeds");
        TestAssert.Near(1.0, (double)waiting.Variable("After")!, 1e-9, "the sequence resumes after the wait");
        TestAssert.Near(1.0, (double)waiting.Variable("Count")!, 1e-9, "the earlier children do not repeat on resume");
        TestAssert.Equal(3, waits, "the wait leaf ran three times");

        var first = new BehaviorTreeInstance(library, "FirstTick", context);
        TestAssert.Equal(BehaviorStatus.Success, first.Tick(0.0, 1.0 / 30.0), "__IsFirstRun is true on the first tick");
        TestAssert.Equal(BehaviorStatus.Failure, first.Tick(1.0 / 30.0, 1.0 / 30.0), "__IsFirstRun is false after it");
    }

    [Check("scripting.behaviour-tree-unknown-leaf-fails-and-is-reported-once")]
    public static void UnknownLeafFailsItsNodeAndIsReportedOnce()
    {
        var reports = new List<string>();
        var context = new BehaviorTreeContext { World = new WorldState(), Sink = (key, _) => reports.Add(key) };
        var instance = new BehaviorTreeInstance(Library(WaitingXml), "Unknown", context);

        TestAssert.Equal(BehaviorStatus.Failure, instance.Tick(0.0, 1.0 / 30.0), "an unknown leaf fails its node");
        TestAssert.Equal(BehaviorStatus.Failure, instance.Tick(1.0 / 30.0, 1.0 / 30.0), "and fails again on the next tick");
        TestAssert.Equal(1, reports.Count, "the unknown type is reported once, not per tick");
        TestAssert.Equal("unimplemented:NoSuchGameAction", reports[0], "the report names the node type");
    }

    private static BehaviorTreeLibrary Library(string xml)
    {
        var library = BehaviorTreeLoader.Parse([("runtime-test.xml", XDocument.Parse(xml))]);
        TestAssert.Equal(0, library.Errors.Count, "the in-test trees are well formed");
        return library;
    }
}
