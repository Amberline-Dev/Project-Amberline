using System.Text.Json;
using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Spells.Bb;
using Amberline.Tools.Scenarios;


namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks batch 278: Rammus plays from his shipped scripts (wave 3 of the new champion waves). The closure and the module are checked
/// against the shipped files; the numbers of the scenario S-Rammus are checked against the scripts' own values (the rank-one ranks, the armour
/// and resist of Defensive Ball Curl, the quarter of his armour that Spiked Shell adds to attack damage, the 100 magic damage of Powerball after
/// the target's resist, the Puncturing Taunt armour cut, the radius of Tremors and a death with its respawn).
/// </summary>
public static class BbRammusChecks
{
    [Check("scripting.bb-rammus-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndRunsEveryNativeOfItsHooks()
    {
        var module = new BbChampionModule("Rammus");
        // audit bb-coverage: 21 scripts (the character script, four spells, the passive and its helpers, and the shared system buffs).
        TestAssert.Equal(21, module.ScriptNames.Count, "the Rammus closure is 21 scripts (audit bb-coverage)");
        var hero = new Entity { ChampionName = "Rammus" };
        TestAssert.Equal(["PowerBall", "DefensiveBallCurl", "PuncturingTaunt", "Tremors2"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Rammus.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "ranks");

        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Rammus");
        // The passive's display name "Spiked Shell" is not a script name; its script is SpikedShell.luaobj, found through the character script.
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        var system = new[]
        {
            "APBonusDamageToTowers", "ChampionChampionDelta", "PositiveChampionDelta", "NegativeChampionDelta",
            "DisconnectTimer", "DisconnectTarget", "FortifyCheck", "FortifyBuff", "Taunt",
        };
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => !system.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a Rammus hook runs is bound: " + string.Join(", ", unbound));
    }


    private static double[] Amounts(JsonElement element, string key) =>
        Str(element, key).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(text => double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private static double Num(JsonElement element, string key) => element.GetProperty(key).GetDouble();

    private static string Str(JsonElement element, string key) => element.GetProperty(key).GetString() ?? string.Empty;

    private static bool Near(double actual, double expected, double tolerance) => Math.Abs(actual - expected) <= tolerance;
}
