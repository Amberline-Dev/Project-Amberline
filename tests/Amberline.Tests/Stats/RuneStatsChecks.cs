using System;
using System.Linq;
using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation;
using Amberline.Simulation.Stats;

namespace Amberline.Tests.Stats;

public static class RuneStatsChecks
{
    [Check("stats.hero.runes")]
    public static void RuneModifiersRemainSeparateFromInventoryAndResources()
    {
        var data = new GameData();
        var items = new ItemCatalog(data.Paths);
        var runes = new RuneCatalog(items, data.Paths);
        var page = RunePage.Create(runes,
            Enumerable.Repeat(5001, RunePage.RedSlots),
            Enumerable.Repeat(5031, RunePage.BlueSlots),
            Enumerable.Repeat(5061, RunePage.YellowSlots),
            new[] { 5366, 5367, 5368 });

        var baseAnnie = data.StatsAtLevel(data.Champion("Annie"), 1);
        var stats = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("Annie", 1, Inventory.Empty(), runes: page));
        TestAssert.Near(baseAnnie.AttackDamage + page.Modifiers["FlatPhysicalDamageMod"],
            stats.AttackDamage, 1e-9);
        TestAssert.Near(1.0, stats.GoldPer10Mod, 1e-9);
        TestAssert.Near(0.02, stats.ExperiencePercent, 1e-9);
        TestAssert.Near(0.05000000074505806, stats.TimeDeadPercent, 1e-12);

        var baseEnergy = data.StatsAtLevel(data.Champion("LeeSin"), 1);
        var energy = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("LeeSin", 1, Inventory.Empty(), runes: page));
        TestAssert.Near(baseEnergy.MaxMp, energy.MaxMp, 1e-9,
            "mana item/rune fields must not leak into energy pool");
        TestAssert.Near(baseEnergy.MpRegen, energy.MpRegen, 1e-9,
            "mana item/rune fields must not leak into energy regeneration");
        TestAssert.Near(baseEnergy.MaxMp, energy.EnergyPool, 1e-9);
        TestAssert.Near(baseEnergy.MpRegen, energy.EnergyRegen, 1e-9);

        var armorPage = RunePage.Create(runes, red: Enumerable.Repeat(5010, RunePage.RedSlots));
        var armorAtOne = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("Annie", 1, Inventory.Empty(), runes: armorPage));
        var armorAtEighteen = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("Annie", 18, Inventory.Empty(), runes: armorPage));
        TestAssert.Near(9 * 0.02500000037252903, armorAtOne.ArmorPenFlat, 1e-9);
        TestAssert.Near(9 * 0.02500000037252903 * 18, armorAtEighteen.ArmorPenFlat, 1e-9);

        var manaPage = RunePage.Create(runes, red: Enumerable.Repeat(5025, RunePage.RedSlots));
        var mana = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("Annie", 1, Inventory.Empty(), runes: manaPage));
        TestAssert.Near(baseAnnie.MaxMp + 9 * 3.2813000679016113, mana.MaxMp, 1e-9);

        var empty = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("Annie", 1, Inventory.Empty(), runes: RunePage.Empty));
        TestAssert.Near(baseAnnie.MaxMp, empty.MaxMp, 1e-9);
        TestAssert.Near(baseAnnie.AttackDamage, empty.AttackDamage, 1e-9);

        // These records exercise the raw r* fields that are not represented
        // by the ordinary FlatMP/FlatEnergy aliases. Their DATA values are
        // already per-second for regeneration, despite the /5 UI text.
        var resourcePage = RunePage.Create(runes,
            red: new[] { 5026, 5028 },
            blue: new[] { 5371, 5372 },
            yellow: new[] { 5369, 5370 },
            black: new[] { 5373, 5374 });
        var energyOne = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("LeeSin", 1, Inventory.Empty(), runes: resourcePage));
        var energyEighteen = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("LeeSin", 18, Inventory.Empty(), runes: resourcePage));
        // Section-kind 2 is a byte read as the client's float32 (float)byte * 0.1f (batch 64): 22 -> 2.2000000477, 54 -> 5.4000000954.
        const double flatEnergyPool = 2.200000047683716;
        const double perLevelEnergyPool = 0.16099999845027924;
        const double greaterEnergyPool = 5.400000095367432;
        TestAssert.Near(baseEnergy.MaxMp + flatEnergyPool + perLevelEnergyPool + greaterEnergyPool,
            energyOne.EnergyPool, 1e-9);
        TestAssert.Near(baseEnergy.MaxMp + flatEnergyPool + perLevelEnergyPool * 18 + greaterEnergyPool,
            energyEighteen.EnergyPool, 1e-9);
        TestAssert.Near(baseEnergy.MpRegen + 0.126000002026558 +
            0.0128300003707409 + 0.314999997615814, energyOne.EnergyRegen, 1e-9);
        TestAssert.Near(baseEnergy.MpRegen + 0.126000002026558 +
            0.0128300003707409 * 18 + 0.314999997615814,
            energyEighteen.EnergyRegen, 1e-9);

        var manaOne = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("Annie", 1, Inventory.Empty(), runes: resourcePage));
        var manaEighteen = HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("Annie", 18, Inventory.Empty(), runes: resourcePage));
        TestAssert.Near(baseAnnie.MaxMp + 0.648100018501282, manaOne.MaxMp, 1e-9);
        TestAssert.Near(data.StatsAtLevel(data.Champion("Annie"), 18).MaxMp +
            0.648100018501282 * 18, manaEighteen.MaxMp, 1e-9);
        TestAssert.Near(baseAnnie.MpRegen + 0.00853999983519316, manaOne.MpRegen, 1e-9);
        TestAssert.Near(data.StatsAtLevel(data.Champion("Annie"), 18).MpRegen +
            0.00853999983519316 * 18, manaEighteen.MpRegen, 1e-9);
    }
}
