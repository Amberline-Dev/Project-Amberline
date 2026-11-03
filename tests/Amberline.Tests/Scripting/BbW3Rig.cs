using System.Reflection;
using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Tests;

/// <summary>
/// The rig of the wave 3 hook checks (W31b, E06 E07 E12): a production <see cref="BbM3.Match"/> whose Ryze hero runs a second Ryze module with a
/// probe closure (a hand-built character script, hand-built buff scripts added to the hero), the way the event hook checks do. The probes write what
/// the hook read into <c>InstanceVars</c> of the buff or <c>CharVars</c> of the hero, and the checks read the production producers' effect there.
/// </summary>
internal sealed class BbW3Rig : IDisposable
{
    public BbW3Rig(BbScript? character, params BbScript[] buffs)
    {
        Match = new BbM3.Match("Ryze");
        var real = new BbScriptResolver(GameData.Default.Paths).Resolve("Ryze");
        var scripts = new List<BbClosureScript>();
        if (character is not null)
            scripts.Add(new BbClosureScript(character.Name, character.SourcePath, BbScriptRole.Character, nameof(BbW3Rig), character));
        foreach (var buff in buffs)
            scripts.Add(new BbClosureScript(buff.Name, buff.SourcePath, BbScriptRole.Referenced, nameof(BbW3Rig), buff));
        scripts.AddRange(real.Scripts.Where(script => character is null || script.Role != BbScriptRole.Character));
        var closure = new BbClosure(real.Champion, scripts, real.Dangling, real.DataOnly, real.Diagnostics);
        Module = new BbChampionModule("Ryze");
        typeof(BbChampionModule).GetField("_scripts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Module, new BbScriptSet(closure));
        var dispatcher = (ChampionSpellDispatcher)Match.Runtime.Dependencies.ChampionDispatcher;
        var field = typeof(ChampionSpellDispatcher).GetField("_modules", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var modules = ((IReadOnlyList<IChampionSpellModule>)field.GetValue(dispatcher)!).ToArray();
        var index = Array.FindIndex(modules, module => ReferenceEquals(module, Match.Module));
        TestAssert.True(index >= 0, "production registers the script-driven Ryze");
        modules[index] = Module;
        field.SetValue(dispatcher, modules);
        Hooks = (MatchGameplayHooks)Match.Runtime.Dependencies.ChampionHooks;
        Hooks.ScriptHooks = Module.Bind(Match.Runtime.Spells);
        Module.ActivateHero(Match.Hero);
    }

    public BbM3.Match Match { get; }
    public BbChampionModule Module { get; }
    public MatchGameplayHooks Hooks { get; }
    public BbTable CharVars(Entity hero) => Module.CharVars(hero);

    /// <summary>Puts the probe buff on <paramref name="holder"/> (added by <paramref name="caster"/>, REPLACE_EXISTING, effectively endless).</summary>
    public BbBuffInstance Give(Entity holder, BbScript buff, Entity? caster = null)
    {
        var state = Module.Buffs!.Add(new BbBuffRequest(holder, caster ?? holder, buff.Name, (int)BbBuffAddType.BUFF_REPLACE_EXISTING,
            (int)BbBuffType.BUFF_Internal, 1, 25000.0, 0.0, false, new BbTable()));
        return Module.Buffs.InstanceOf(state ?? throw new InvalidOperationException($"{buff.Name} was not added"))
               ?? throw new InvalidOperationException($"{buff.Name} has no script instance");
    }

    public void NoFailures() =>
        TestAssert.Equal(0, Module.ScriptFailures.Count, "no script failure: " + string.Join("; ", Module.ScriptFailures.Select(f => $"{f.Script}.{f.Hook} {f.Message}")));

    public void Dispose() => Match.Dispose();

    // Hand-built scripts -----------------------------------------------------------------------------------------------------------------------

    public static BbBlock Block(string function, params object[] parameters) => BbTest.Block(function, parameters);

    /// <summary><c>InstanceVars[name] = vars[from]</c>.</summary>
    public static BbBlock Keep(string name, string from) =>
        BbTest.Block("BBSetVarInTable", ["DestVar", name, "DestVarTable", "InstanceVars", "SrcVar", from]);

    /// <summary><c>CharVars[name] = vars[from]</c>.</summary>
    public static BbBlock KeepChar(string name, string from) =>
        BbTest.Block("BBSetVarInTable", ["DestVar", name, "DestVarTable", "CharVars", "SrcVar", from]);

    /// <summary><c>InstanceVars[name] = InstanceVars[name] + 1</c>.</summary>
    public static BbBlock Count(string name) =>
        BbTest.Block("BBMath", ["Src1Var", name, "Src1VarTable", "InstanceVars", "Src1Value", 0, "Src2Value", 1, "DestVar", name, "DestVarTable", "InstanceVars",
            "MathOp", BbTest.Ref("MO_ADD")]);

    public static BbScript Buff(string name, params BbEventTable[] hooks) => new(name, $"Spells/{name}.luaobj", [], hooks, []);

    public static BbScript Character(string name, params BbEventTable[] hooks) => new(name, $"Characters/W3/Scripts/{name}.luaobj", [], hooks, []);
}
