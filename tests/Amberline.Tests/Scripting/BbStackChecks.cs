using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, wave 3 (W31b, E29): <c>BBSpellBuffAdd</c> with <c>NumberOfStacks</c> above one, the stack of a script buff as a record
/// of its own (the variables its activation worked on, its own deactivation), <c>BBSpellBuffRemove</c> taking one stack, and the stacks of a
/// BUFF_STACKS_AND_RENEWS buff belonging to the unit that added them. The buffs are the shipped scripts: Ezreal's Rising Spell Force (a glow per
/// stack, removed with its stack; also the buff the <c>RENEW_EXISTING</c> checks use, as Irelia's Transcendent Blades does with four stacks spent
/// one per cast) and Vayne's Silver Bolts mark (counted and removed by caster). Written, not run (the standing rule of the engine batches).
/// </summary>
public static class BbStackChecks
{
    private const string Passive = "EzrealRisingSpellForce";
    private const string Mark = "VayneSilveredDebuff";

    private static BuffState? Add(BbChampionModule module, Entity target, Entity caster, string name, BbBuffAddType type, int maxStack, double duration,
        int stacks) =>
        module.Buffs!.Add(new BbBuffRequest(target, caster, name, (int)type, (int)BbBuffType.BUFF_CombatEnchancer, maxStack, duration, 0.0, false,
            new BbTable(), stacks));

    private static int Stacks(Entity unit, string name, Entity? caster = null) =>
        unit.Buffs.Where(buff => buff.Name == name && (caster is null || buff.CasterNetId == caster.NetId)).Sum(buff => buff.Stacks);

    [Check("scripting.bb-stacks-area-add-buff-without-a-stack-count-adds-one-stack")]
    public static void AreaAddBuffWithoutAStackCountAddsOne()
    {
        // Batch 284 (N6): the client's area-add routine (BBForEachUnitInTargetAreaAddBuff's body) reads BuffNumberOfStacks with the default 1, so a call
        // that does not name it adds one stack to every admitted unit.
        using var match = new BbM3.Match("Ezreal");
        var script = new BbScript("W284AreaAdd", "Spells/W284AreaAdd.luaobj", [],
            [new BbEventTable("TargetExecute", [BbTest.Block("BBForEachUnitInTargetAreaAddBuff", ["AttackerVar", "Owner", "CenterVar", "Owner",
                "Range", 300, "Flags", "AffectFriends AffectHeroes ", "BuffAttackerVar", "Owner", "BuffName", Passive,
                "BuffAddType", BbTest.Ref("BUFF_STACKS_AND_RENEWS"), "BuffType", BbTest.Ref("BUFF_CombatEnchancer"), "BuffMaxStack", 5,
                "BuffDuration", 6])])], []);
        match.Module.Router!.RunTargetExecute(script, match.Hero, 0, 1, 0.0, new Position3(0, 0, 0), new Position3(0, 0, 0), match.Hero);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure: " + string.Join("; ", match.Module.ScriptFailures.Select(f => f.Message)));
        TestAssert.Equal(1, Stacks(match.Hero, Passive), "a call without BuffNumberOfStacks adds one stack");
    }

    [Check("scripting.bb-stacks-number-of-stacks-adds-that-many-stacks-each-its-own-record-and-activation")]
    public static void NumberOfStacksAddsStacks()
    {
        using var match = new BbM3.Match("Ezreal");
        var module = match.Module;
        var state = Add(module, match.Hero, match.Hero, Passive, BbBuffAddType.BUFF_STACKS_AND_RENEWS, 5, 6.0, 3);
        TestAssert.True(state is not null, "the buff was added");
        TestAssert.Equal(3, state!.Stacks, "three stacks in one add");
        var instance = module.Buffs!.InstanceOf(state) ?? throw new InvalidOperationException("no script instance");
        TestAssert.Equal(3, instance.Stacks.Count, "a record per stack");
        TestAssert.Equal(3, instance.Stacks.Select(stack => stack.Vars).Distinct().Count(), "each stack worked on a table of its own");
        TestAssert.Equal(3, instance.Stacks.Count(stack => stack.Vars["Particle"] is double id && id > 0.0), "each stack's activation created its glow");
        TestAssert.True(ReferenceEquals(instance.InstanceVars, instance.Stacks[^1].Vars), "the hooks that run once per buff read the newest stack's table");
        TestAssert.Equal(0, module.ScriptFailures.Count, "no script failure");
        var more = Add(module, match.Hero, match.Hero, Passive, BbBuffAddType.BUFF_STACKS_AND_RENEWS, 5, 6.0, 4);
        TestAssert.Equal(5, more!.Stacks, "the count stops at MaxStack");
        TestAssert.Equal(5, instance.Stacks.Count, "and so does the number of records");
    }

    [Check("scripting.bb-stacks-buff-remove-takes-one-stack-with-its-own-deactivation-and-the-last-takes-the-buff")]
    public static void RemoveTakesOneStack()
    {
        using var match = new BbM3.Match("Ezreal");
        var module = match.Module;
        var state = Add(module, match.Hero, match.Hero, Passive, BbBuffAddType.BUFF_STACKS_AND_RENEWS, 5, 6.0, 3)!;
        var instance = module.Buffs!.InstanceOf(state)!;
        var oldest = instance.Stacks[0].Vars["Particle"];
        module.Buffs.Remove(match.Hero, Passive, match.Hero);
        TestAssert.Equal(2, Stacks(match.Hero, Passive), "one stack less");
        TestAssert.Equal(2, instance.Stacks.Count, "and one record less");
        TestAssert.True(match.Log.Killed.Contains((uint)(double)oldest!), "the effect of the ended stack was removed by its own deactivation");
        TestAssert.Equal(1, match.Log.Killed.Count, "and only that one");
        module.Buffs.Remove(match.Hero, Passive, match.Hero);
        module.Buffs.Remove(match.Hero, Passive, match.Hero);
        TestAssert.Equal(0, Stacks(match.Hero, Passive), "the last stack takes the buff");
        TestAssert.Equal(3, match.Log.Killed.Count, "every glow is gone, none twice");
        TestAssert.Equal(0, module.ScriptFailures.Count, "no script failure");
    }

    [Check("scripting.bb-stacks-the-buff-ending-ends-every-stack-with-its-own-deactivation-and-clear-removes-them-whole")]
    public static void EndingAndClear()
    {
        using var match = new BbM3.Match("Ezreal");
        var module = match.Module;
        Add(module, match.Hero, match.Hero, Passive, BbBuffAddType.BUFF_STACKS_AND_RENEWS, 5, 6.0, 4);
        module.Buffs!.RemoveAll(match.Hero, Passive, null);
        TestAssert.Equal(0, Stacks(match.Hero, Passive), "BBSpellBuffClear takes every stack");
        TestAssert.Equal(4, match.Log.Killed.Count, "each of the four stacks removed its own glow");
        Add(module, match.Hero, match.Hero, Passive, BbBuffAddType.BUFF_STACKS_AND_RENEWS, 5, 1.0, 2);
        match.Advance(2.0);
        TestAssert.Equal(0, Stacks(match.Hero, Passive), "the buff runs out");
        TestAssert.Equal(6, match.Log.Killed.Count, "and both stacks removed their glow");
    }

    [Check("scripting.bb-stacks-remove-stacks-ends-several-in-one-go-and-takes-the-buff-when-it-asks-for-more")]
    public static void RemoveStacksTogether()
    {
        using var match = new BbM3.Match("Ezreal");
        var module = match.Module;
        Add(module, match.Hero, match.Hero, Passive, BbBuffAddType.BUFF_STACKS_AND_RENEWS, 5, 6.0, 4);
        module.Buffs!.RemoveStacks(match.Hero, Passive, match.Hero, 2);
        TestAssert.Equal(2, Stacks(match.Hero, Passive), "two of four stacks went");
        TestAssert.Equal(2, match.Log.Killed.Count, "each of the two removed its own glow");
        module.Buffs.RemoveStacks(match.Hero, Passive, match.Hero, 5);
        TestAssert.Equal(0, Stacks(match.Hero, Passive), "a request for more than the buff holds takes the buff");
        TestAssert.Equal(4, match.Log.Killed.Count, "and the other two glows");
        TestAssert.Equal(0, module.ScriptFailures.Count, "no script failure");
    }

    [Check("scripting.bb-stacks-remove-with-a-reset-duration-restarts-the-stacks-that-stay")]
    public static void RemoveWithResetDuration()
    {
        using var match = new BbM3.Match("Ezreal");
        var module = match.Module;
        Add(module, match.Hero, match.Hero, Passive, BbBuffAddType.BUFF_STACKS_AND_RENEWS, 5, 10.0, 3);
        module.Buffs!.Remove(match.Hero, Passive, match.Hero, 3.0);
        var left = match.Hero.Buffs.Single(buff => buff.Name == Passive);
        TestAssert.Equal(2, left.Stacks, "one stack went");
        TestAssert.Near(3.0, left.ExpiresAt, 1e-6, "the two that stay run for the three seconds the script named");
        module.Buffs.Remove(match.Hero, Passive, match.Hero);
        TestAssert.Near(3.0, match.Hero.Buffs.Single(buff => buff.Name == Passive).ExpiresAt, 1e-6, "a removal with no reset leaves the timer alone");
        TestAssert.Equal(0, module.ScriptFailures.Count, "no script failure");
    }

    [Check("scripting.bb-stacks-renew-existing-with-a-count-creates-and-raises-to-the-count-and-a-renewal-adds-none")]
    public static void RenewRaisesToTheCount()
    {
        using var match = new BbM3.Match("Ezreal");
        var module = match.Module;
        var state = Add(module, match.Hero, match.Hero, Passive, BbBuffAddType.BUFF_RENEW_EXISTING, 4, 10.0, 4);
        TestAssert.Equal(4, state!.Stacks, "RENEW_EXISTING with NumberOfStacks 4 creates the buff with four stacks");
        module.Buffs!.Remove(match.Hero, Passive, match.Hero);
        module.Buffs.Remove(match.Hero, Passive, match.Hero);
        TestAssert.Equal(2, Stacks(match.Hero, Passive), "a single removal spends one stack of four (Irelia's Transcendent Blades)");
        Add(module, match.Hero, match.Hero, Passive, BbBuffAddType.BUFF_RENEW_EXISTING, 4, 10.0, 1);
        TestAssert.Equal(2, Stacks(match.Hero, Passive), "a renewal adds no stack");
        Add(module, match.Hero, match.Hero, Passive, BbBuffAddType.BUFF_RENEW_EXISTING, 4, 10.0, 4);
        TestAssert.Equal(4, Stacks(match.Hero, Passive), "a renewal that names four stacks raises the count to four");
        TestAssert.Equal(0, module.ScriptFailures.Count, "no script failure");
    }

    [Check("scripting.bb-stacks-two-casters-have-a-mark-each-counted-and-removed-by-caster")]
    public static void TwoCastersTwoMarks()
    {
        using var match = new BbM3.Match("Vayne");
        var module = match.Module;
        var other = match.Ally;
        Add(module, match.Foe, match.Hero, Mark, BbBuffAddType.BUFF_STACKS_AND_RENEWS, 3, 3.5, 1);
        Add(module, match.Foe, other, Mark, BbBuffAddType.BUFF_STACKS_AND_RENEWS, 3, 3.5, 1);
        Add(module, match.Foe, match.Hero, Mark, BbBuffAddType.BUFF_STACKS_AND_RENEWS, 3, 3.5, 1);
        TestAssert.Equal(2, match.Foe.Buffs.Count(buff => buff.Name == Mark), "one mark per caster");
        TestAssert.Equal(2, Stacks(match.Foe, Mark, match.Hero), "Vayne's mark counts her two stacks (BBGetBuffCountFromCaster)");
        TestAssert.Equal(1, Stacks(match.Foe, Mark, other), "the other caster's mark counts one");
        TestAssert.Equal(3, Stacks(match.Foe, Mark), "and BBGetBuffCountFromAll counts both");
        module.Buffs!.Remove(match.Foe, Mark, other);
        TestAssert.Equal(2, Stacks(match.Foe, Mark), "removing the other caster's stack leaves Vayne's two");
        TestAssert.Equal(0, module.ScriptFailures.Count, "no script failure");
    }

    [Check("scripting.bb-stacks-buff-remove-takes-the-oldest-application-of-the-attacker")]
    public static void RemoveTakesTheOldestApplication()
    {
        // Batch 292 (N15): BBSpellBuffRemove finds the one instance of the script (the script's instance map) and the removal erases the
        // first entry of the Attacker. Two overlapping applications, the older one with more time left: the older one goes, whatever its timer.
        // Before batch 292 the one with the least time left went.
        using var match = new BbM3.Match("Ezreal");
        var module = match.Module;
        var older = Add(module, match.Hero, match.Hero, Passive, BbBuffAddType.BUFF_STACKS_AND_OVERLAPS, 5, 10.0, 1)!;
        var newer = Add(module, match.Hero, match.Hero, Passive, BbBuffAddType.BUFF_STACKS_AND_OVERLAPS, 5, 2.0, 1)!;
        TestAssert.Equal(2, match.Hero.Buffs.Count(buff => buff.Name == Passive), "two applications");
        module.Buffs!.Remove(match.Hero, Passive, match.Hero);
        TestAssert.False(match.Hero.Buffs.Contains(older), "the older application went");
        TestAssert.True(match.Hero.Buffs.Contains(newer), "the newer one, with less time left, stays");
        TestAssert.Equal(0, module.ScriptFailures.Count, "no script failure");
    }

    [Check("scripting.bb-stacks-a-buff-removing-itself-by-name-from-its-own-hook-ends-whole")]
    public static void SelfRemovalEndsWhole()
    {
        using var match = new BbM3.Match("Vayne");
        var module = match.Module;
        // The third stack of the mark spends all three stacks from its own activation (BBSpellBuffRemoveStacks 3).
        for (var index = 0; index < 3; index++)
            Add(module, match.Foe, match.Hero, Mark, BbBuffAddType.BUFF_STACKS_AND_RENEWS, 3, 3.5, 1);
        TestAssert.Equal(0, Stacks(match.Foe, Mark), "the third stack consumed the mark");
        TestAssert.Equal(0, module.ScriptFailures.Count, "no script failure");
    }

    [Check("scripting.bb-stacks-a-self-removing-hook-runs-per-stack-and-each-removal-erases-the-oldest-stack")]
    public static void SelfRemovingHookRunsPerStack()
    {
        // Retail keeps an entry per stack and runs each stack's hooks on its own InstanceVars; BBSpellBuffRemove erases the
        // first entry of the Attacker per call, whichever stack's hook calls it. Three stacks with Drop 1, 0, 1: the update hook
        // runs three times, the first and the third remove one stack each (the oldest), one stack stays. Before batch 241 the hook ran once on
        // the newest stack's table and its self-removal ended the buff whole.
        const string name = "W241SelfDrop";
        static BbBlock Add1(string variable) => BbTest.Block("BBMath", ["Src1Var", variable, "Src1VarTable", "CharVars", "Src1Value", 0, "Src2Value", 1,
            "DestVar", variable, "DestVarTable", "CharVars", "MathOp", BbTest.Ref("MO_ADD")]);
        var buff = BbW3Rig.Buff(name,
            new BbEventTable("BuffOnUpdateActions", [
                Add1("Runs"),
                BbTest.Block("BBIf", ["Src1Var", "Drop", "Src1VarTable", "InstanceVars", "Value2", 1, "CompareOp", BbTest.Ref("CO_EQUAL")],
                    BbTest.Block("BBSpellBuffRemove", ["TargetVar", "Owner", "AttackerVar", "Owner", "BuffName", name]))]),
            new BbEventTable("OnBuffDeactivate", [Add1("Ended")]));
        using var rig = new BbW3Rig(null, buff);
        var hero = rig.Match.Hero;
        foreach (var drop in new[] { 1.0, 0.0, 1.0 })
        {
            var vars = new BbTable();
            vars["Drop"] = drop;
            rig.Module.Buffs!.Add(new BbBuffRequest(hero, hero, name, (int)BbBuffAddType.BUFF_STACKS_AND_RENEWS, (int)BbBuffType.BUFF_Internal, 3,
                25000.0, 0.0, false, vars));
        }
        TestAssert.Equal(3, Stacks(hero, name), "three stacks");
        // Batch 720 (owner decision, 8 November 2026): the update hook runs on the hero's 0.25 s pass, the first time on his first pass after
        // the adds. The adds come before the match's first step, so that pass is the first step (his line's first poll) and the next is 8
        // steps later, at +0.300, the end of the 0.3 s this check used to advance (batch 241's phase ran the first update at +0.267 and the
        // next at +0.5). One pass is one update: the state the check reads is the first pass's, and the next pass is pinned as well.
        var spells = rig.Match.Runtime.Spells;
        double Runs() => rig.CharVars(hero)["Runs"] as double? ?? 0.0;
        var steps = 0;
        do { rig.Match.Advance(BbM3.Step); steps++; } while (!spells.SelfUpdateDue(hero) && steps < 30);
        rig.NoFailures();
        TestAssert.Near(3.0, Runs(), 1e-9, "the update hook ran once per stack");
        TestAssert.Equal(1, Stacks(hero, name), "two of the runs removed a stack each");
        TestAssert.Near(2.0, rig.CharVars(hero)["Ended"] as double? ?? 0.0, 1e-9, "each erased stack ran its own deactivation");
        // Nothing runs until his next pass; there the stack that stayed (the third add, Drop 1) runs alone and erases itself, the last stack.
        do
        {
            rig.Match.Advance(BbM3.Step);
            steps++;
            if (!spells.SelfUpdateDue(hero)) TestAssert.Near(3.0, Runs(), 1e-9, $"no run off the hero's passes (step {steps})");
        } while (!spells.SelfUpdateDue(hero) && steps < 60);
        rig.NoFailures();
        TestAssert.Near(4.0, Runs(), 1e-9, "the next pass ran the one stack left, once");
        TestAssert.Equal(0, Stacks(hero, name), "its self-removal took the last stack");
        TestAssert.Near(3.0, rig.CharVars(hero)["Ended"] as double? ?? 0.0, 1e-9, "with its own deactivation");
    }
}
