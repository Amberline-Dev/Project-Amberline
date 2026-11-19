using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation;
using Amberline.Simulation.Stats;

namespace Amberline.Tests;

/// <summary>
/// Batch 902 (player report on 1.0.0.132, 19 November 2026: "siege commander mastery doesnt work - check all masteries"), the 1.0.0.126 side:
/// the 126 tree (DATA/Talents/TalentTree.xml ids 100-148) has no Siege Commander; this pins that every stat talent's own number reaches the hero.
/// Pages are legal 126 pages (rows 0/4/8/12/16/20 per group; 107←102, 112←110, 127←114, 113←109 at the prerequisite's maximum rank).
/// </summary>
public static class B902MasteryChecks
{
    /// <summary>
    /// The talents whose whole effect is a <c>BBIncStat</c>/<c>BBInc*PAR*</c> in <c>UpdateSelfBuffStats</c> (Talents/&lt;id&gt;.luaobj): each
    /// one's number reaches the hero's stats (<c>HeroStatCalculator</c>, the production refresh's calculator), measured as the difference
    /// between a legal page with the talent and the same page without it, at champion level 18.
    /// </summary>
    [Check("masteries-b902-p126-stat-talents-reach-the-hero")]
    public static void StatTalentsReachTheHero()
    {
        var data = GameData.Default;
        var items = new ItemCatalog(data.Paths);
        var catalog = new MasteryCatalog();
        var empty = new InventorySlot?[] { null, null, null, null, null, null };
        HeroStats Stats(Dictionary<int, int> ranks) => HeroStatCalculator.Calculate(data, items,
            new HeroStatsInput("Annie", 18, empty, masteries: ranks.Count == 0 ? MasteryPage.Empty : MasteryPage.Create(catalog, ranks)));
        var o1 = new Dictionary<int, int> { [103] = 3, [136] = 1 };
        var o2 = new Dictionary<int, int>(o1) { [105] = 4 };
        var o3 = new Dictionary<int, int>(o2) { [137] = 2, [146] = 1, [101] = 1 };
        var o4 = new Dictionary<int, int>(o3) { [100] = 3, [106] = 1 };
        var d1 = new Dictionary<int, int> { [133] = 1, [110] = 3 };
        var d3 = new Dictionary<int, int>(d1) { [129] = 1, [114] = 4, [144] = 2, [127] = 1 };
        var u1 = new Dictionary<int, int> { [124] = 1, [134] = 3 };
        var u2 = new Dictionary<int, int>(u1) { [115] = 4 };
        var u3 = new Dictionary<int, int>(u2) { [121] = 3, [135] = 1 };
        var u4 = new Dictionary<int, int>(u3) { [119] = 3, [140] = 1 };
        var cases = new (string Name, Dictionary<int, int> Base, int Id, int Rank, Func<HeroStats, double> Read, double Expected)[]
        {
            ("100 Brute Force", o3, 100, 3, s => s.AttackDamage, 3.0),
            ("101 Sunder", o2, 101, 3, s => s.ArmorPenFlat, 6.0),
            ("102 Sorcery", o1, 102, 4, s => s.CooldownMod, -0.03),
            ("103 Deadliness", new() { [136] = 1 }, 103, 3, s => s.CritChance, 0.0198),
            ("104 Lethality", o4, 104, 3, s => s.CritMultiplier, 0.1),
            ("105 Alacrity", o1, 105, 4, s => s.AttackSpeedMod, double.NaN),
            ("106 Archmage's Savvy", new() { [136] = 1 }, 106, 3, s => s.AbilityPower, 0.6 * 18),
            ("107 Archaic Knowledge", new(o1) { [102] = 4 }, 107, 1, s => s.MagicPenPercent, 0.15),
            ("108 Veteran's Scars", d3, 108, 4, s => s.MaxHp, 48.0),
            ("109 Hardiness", new() { [133] = 1 }, 109, 3, s => s.Armor, 6.0),
            ("110 Resistance", new() { [133] = 1 }, 110, 3, s => s.SpellBlock, 6.0),
            ("114 Evasion", d1, 114, 4, s => s.Dodge, 0.02),
            ("115 Expanded Mind", u1, 115, 4, s => s.MaxMp, double.NaN),
            ("116 Awareness", u1, 116, 4, s => s.ExperiencePercent, 0.05),
            ("117 Greed", u2, 117, 1, s => s.GoldPer10Mod, 1.0),
            ("118 Intelligence", u4, 118, 3, s => s.CooldownMod, -0.06),
            ("119 Quickness", u3, 119, 3, s => s.MoveSpeed, double.NaN),
            ("121 Meditation", u2, 121, 3, s => s.MpRegen, 0.6),
            ("132 Perseverence", new() { [124] = 1 }, 132, 3, s => s.HpRegen, double.NaN),
            ("134 Good Hands", new() { [124] = 1 }, 134, 3, s => s.TimeDeadPercent, 0.1),
        };
        var failures = new List<string>();
        foreach (var (name, basis, id, rank, read, expected) in cases)
        {
            HeroStats without, with;
            try
            {
                without = Stats(basis);
                with = Stats(new Dictionary<int, int>(basis) { [id] = rank });
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: page refused ({ex.Message})");
                continue;
            }
            var delta = read(with) - read(without);
            // Expanded Mind: 5 % of the mana pool; Quickness: 3 % of the speed (below the 415 soft cap); Alacrity and Perseverence: the
            // calculator's own composition (audit A3-R), so only the direction is asserted.
            var want = id == 115 ? read(without) * 0.05 : id == 119 ? read(without) * 0.03 : expected;
            if (double.IsNaN(want))
            {
                if (delta <= 0.0) failures.Add($"{name}: {delta:F6}, more wanted");
                continue;
            }
            if (Math.Abs(delta - want) > 1e-6) failures.Add($"{name}: {delta:F6}, want {want:F6}");
        }
        TestAssert.True(failures.Count == 0, "stat talents: " + string.Join("; ", failures));
    }
}
