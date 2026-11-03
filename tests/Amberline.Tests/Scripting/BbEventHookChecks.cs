using System.Reflection;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Runtime;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Spells.Champions;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine, Phase 3 (W22): the unit-event hooks the router did not run and the ten hand-written champions' scripts listen
/// to: <c>BuffOnPreAttack</c> / <c>CharOnPreAttack</c> (Shaco's Backstab and Deceive), <c>BuffOnLevelUp</c> / <c>CharOnLevelUp</c> (Yorick's
/// Techmaturgical Icon), <c>BuffOnLevelUpSpell</c> (Nidalee's Aspect of the Cougar, Yorick) and <c>CharOnResurrect</c> / <c>BuffOnResurrect</c>
/// (Vayne, Yorick, Orianna). The probe scripts are hand-built block lists in a production <c>MatchRuntime</c> whose Ryze module is a second
/// instance with a probe closure (the rig of the missile checks); the events come from the production producers.
/// </summary>
public static class BbEventHookChecks
{
    private static BbBlock Set(string name, string from, string? table = "InstanceVars") =>
        table is null ? BbTest.Block("BBSetVarInTable", ["DestVar", name, "SrcVar", from])
                      : BbTest.Block("BBSetVarInTable", ["DestVar", name, "DestVarTable", table, "SrcVar", from]);

    private static BbScript ProbeBuff() => new("W22EventProbe", "Spells/W22EventProbe.luaobj", [],
    [
        new BbEventTable("BuffOnPreAttack", [Set("PreTarget", "Target"), Set("PreOwner", "Owner"), Set("PreAttacker", "Attacker")]),
        new BbEventTable("BuffOnLevelUp", [Set("UpOwner", "Owner"), Set("UpAttacker", "Attacker")]),
        new BbEventTable("BuffOnLevelUpSpell", [Set("SpellUpSlot", "Slot"), Set("SpellUpOwner", "Owner")]),
        new BbEventTable("BuffOnResurrect", [Set("BuffResOwner", "Owner")]),
    ], []);

    private static BbScript ProbeCharacter() => new("W22CharProbe", "Characters/W22/Scripts/W22CharProbe.luaobj", [],
    [
        new BbEventTable("CharOnPreAttack", [Set("CharPreTarget", "Target", "CharVars")]),
        new BbEventTable("CharOnLevelUp", [Set("CharUpOwner", "Owner", "CharVars")]),
        new BbEventTable("CharOnResurrect", [Set("CharResOwner", "Owner", "CharVars")]),
    ], []);

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            Match = new BbM3.Match("Ryze");
            var real = new BbScriptResolver(GameData.Default.Paths).Resolve("Ryze");
            var probeChar = ProbeCharacter();
            var probeBuff = ProbeBuff();
            var scripts = new List<BbClosureScript>
            {
                new(probeChar.Name, probeChar.SourcePath, BbScriptRole.Character, "BbEventHookChecks", probeChar),
                new(probeBuff.Name, probeBuff.SourcePath, BbScriptRole.Referenced, "BbEventHookChecks", probeBuff),
            };
            scripts.AddRange(real.Scripts.Where(script => script.Role != BbScriptRole.Character));
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
            var state = Module.Buffs!.Add(new BbBuffRequest(Match.Hero, Match.Hero, probeBuff.Name, (int)BbBuffAddType.BUFF_REPLACE_EXISTING,
                (int)BbBuffType.BUFF_Internal, 1, 25000.0, 0.0, false, new BbTable()));
            Probe = Module.Buffs.InstanceOf(state ?? throw new InvalidOperationException("the probe buff was not added"))
                    ?? throw new InvalidOperationException("the probe buff has no script instance");
        }

        public BbM3.Match Match { get; }
        public BbChampionModule Module { get; }
        public MatchGameplayHooks Hooks { get; }
        public BbBuffInstance Probe { get; }
        public BbTable CharVars => Module.CharVars(Match.Hero);

        public void NoFailures() =>
            TestAssert.Equal(0, Module.ScriptFailures.Count, "no script failure: " + string.Join("; ", Module.ScriptFailures.Select(f => $"{f.Script}.{f.Hook} {f.Message}")));

        public void Dispose() => Match.Dispose();
    }

    [Check("scripting.bb-hook-pre-attack-runs-on-the-attackers-buffs-and-character-script-with-the-victim")]
    public static void PreAttackReachesTheAttackersScripts()
    {
        using var rig = new Rig();
        var hero = rig.Match.Hero;
        var foe = rig.Match.Foe;
        rig.Match.Runtime.Combat.BasicAttackDamage(hero, foe);
        rig.NoFailures();
        TestAssert.True(ReferenceEquals(rig.Probe.InstanceVars["PreTarget"], foe), "Target is the unit attacked");
        TestAssert.True(ReferenceEquals(rig.Probe.InstanceVars["PreOwner"], hero) && ReferenceEquals(rig.Probe.InstanceVars["PreAttacker"], hero),
            "Owner and Attacker are the attacking hero");
        TestAssert.True(ReferenceEquals(rig.CharVars["CharPreTarget"], foe), "CharOnPreAttack of the character script ran with the same Target");
    }

    [Check("scripting.bb-hook-level-up-and-spell-level-up-reach-buffs-and-the-character-script")]
    public static void LevelUpHooksRun()
    {
        using var rig = new Rig();
        var hero = rig.Match.Hero;
        rig.Hooks.OnLevelChanged(hero);
        rig.Module.OnRankUp(rig.Match.Runtime.Spells, hero, 2, 1);
        rig.NoFailures();
        TestAssert.True(ReferenceEquals(rig.Probe.InstanceVars["UpOwner"], hero), "BuffOnLevelUp ran with the hero as Owner");
        TestAssert.True(ReferenceEquals(rig.Probe.InstanceVars["UpAttacker"], hero), "Attacker is the buff's caster");
        TestAssert.True(ReferenceEquals(rig.CharVars["CharUpOwner"], hero), "CharOnLevelUp ran");
        TestAssert.Equal(2.0, (double)rig.Probe.InstanceVars["SpellUpSlot"]!, "BuffOnLevelUpSpell sees the slot that gained the rank");
        TestAssert.True(ReferenceEquals(rig.Probe.InstanceVars["SpellUpOwner"], hero), "and the hero as Owner");
    }

    [Check("scripting.bb-hook-resurrect-runs-when-a-hero-respawns-not-at-the-first-spawn")]
    public static void ResurrectRunsOnRespawn()
    {
        using var rig = new Rig();
        var hero = rig.Match.Hero;
        TestAssert.True(rig.CharVars["CharResOwner"] is null && rig.Probe.InstanceVars["BuffResOwner"] is null, "nothing ran before a respawn");
        rig.Hooks.OnRespawn(hero);
        rig.NoFailures();
        TestAssert.True(ReferenceEquals(rig.CharVars["CharResOwner"], hero), "CharOnResurrect ran with the hero as Owner");
        TestAssert.True(ReferenceEquals(rig.Probe.InstanceVars["BuffResOwner"], hero), "BuffOnResurrect ran on the hero's script buff");
    }

    [Check("scripting.bb-hook-events-are-the-shipped-hooks-of-the-ten-champions")]
    public static void TheShippedScriptsDefineTheHooks()
    {
        var resolver = new BbScriptResolver(GameData.Default.Paths);
        var users = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var champion in new[] { "Annie", "Janna", "Ezreal", "Vayne", "Blitzcrank", "LeeSin", "Nidalee", "Shaco", "Yorick", "Orianna" })
            foreach (var script in resolver.Resolve(champion).Scripts)
                foreach (var table in script.Script.Events)
                    if (table.Hook is "BuffOnPreAttack" or "BuffOnLevelUp" or "BuffOnLevelUpSpell" or "CharOnResurrect" or "BuffOnMoveEnd"
                        or "BuffOnMoveSuccess" or "BuffOnCollisionTerrain")
                    {
                        if (!users.TryGetValue(table.Hook, out var set)) users[table.Hook] = set = new SortedSet<string>(StringComparer.Ordinal);
                        set.Add(champion);
                    }
        TestAssert.True(users["BuffOnPreAttack"].SetEquals(["Shaco", "Yorick"]), "BuffOnPreAttack: " + string.Join(",", users["BuffOnPreAttack"]));
        TestAssert.True(users["CharOnResurrect"].SetEquals(["Orianna", "Vayne", "Yorick"]), "CharOnResurrect: " + string.Join(",", users["CharOnResurrect"]));
        TestAssert.True(users["BuffOnLevelUpSpell"].SetEquals(["Nidalee", "Yorick"]), "BuffOnLevelUpSpell: " + string.Join(",", users["BuffOnLevelUpSpell"]));
        // Nidalee's closure holds her cougar set since the closure references of batch 87 (AspectOfTheCougar pushes Nidalee_Cougar): Pounce ends with BuffOnMoveEnd.
        TestAssert.True(users["BuffOnMoveEnd"].SetEquals(["Blitzcrank", "LeeSin", "Nidalee", "Vayne"]), "BuffOnMoveEnd: " + string.Join(",", users["BuffOnMoveEnd"]));
    }
}
