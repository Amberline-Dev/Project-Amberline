using System;
using System.Linq;
using Amberline.Core;
using Amberline.Data;

namespace Amberline.Tests.Core;

public static class RuneChecks
{
    [Check("data.runes.catalog")]
    public static void CatalogReadsShippedRecordsAndLocalization()
    {
        var catalog = new RuneCatalog();
        TestAssert.Equal(374, catalog.Count);
        TestAssert.Equal(374, catalog.All.Count);
        TestAssert.False(catalog.Contains(RuneCatalog.PlaceholderRuneId));

        var lesserMark = catalog.Get(5001)!;
        TestAssert.Equal(RuneColor.Red, lesserMark.Color);
        TestAssert.Equal(RuneTier.One, lesserMark.Tier);
        TestAssert.Equal("Lesser Mark of Strength", lesserMark.DisplayName);
        TestAssert.Equal("+0.53 attack damage", lesserMark.Description);
        TestAssert.Equal("r_1_1.png", lesserMark.Icon);
        TestAssert.Near(0.524999976158142, lesserMark.Modifier("FlatPhysicalDamageMod"), 1e-12);

        TestAssert.Equal(RuneTier.Two, catalog.Get(5244)!.Tier);
        TestAssert.Equal(RuneTier.Three, catalog.Get(5366)!.Tier);
        TestAssert.Equal("Greater Quintessence of Revival", catalog.Get(5366)!.DisplayName);
        TestAssert.Near(0.05000000074505806,
            catalog.Get(5366)!.Modifier("rPercentTimeDeadMod"), 1e-12);
        TestAssert.Near(0.126000002026558, catalog.Get(5369)!.Modifier("FlatEnergyRegenMod"), 1e-12);
        TestAssert.Near(0.0128300003707409,
            catalog.Get(5370)!.Modifier("rFlatEnergyRegenModPerLevel"), 1e-12);
        // These two values use the shipped Inibin section-kind 2 encoding (a byte read as the
        // client's float32 (float)byte * 0.1f; batch 64): 22 -> 2.2000000477, 54 -> 5.4000000954.
        TestAssert.Near(2.200000047683716,
            catalog.Get(5371)!.Modifier("FlatEnergyPoolMod"), 1e-12);
        TestAssert.Near(0.160999998450279,
            catalog.Get(5372)!.Modifier("rFlatEnergyModPerLevel"), 1e-12);
        TestAssert.Near(0.314999997615814,
            catalog.Get(5373)!.Modifier("FlatEnergyRegenMod"), 1e-12);
        TestAssert.Near(5.400000095367432,
            catalog.Get(5374)!.Modifier("FlatEnergyPoolMod"), 1e-12);
        TestAssert.Near(0.00208300002850592,
            catalog.Get(5019)!.Modifier("rFlatDodgeMod"), 1e-12);
        TestAssert.Near(-0.000908999994862825,
            catalog.Get(5021)!.Modifier("rPercentCooldownMod"), 1e-12);
    }

    [Check("core-runes.page-validation")]
    public static void PageValidatesColorsAndOwnsInput()
    {
        var catalog = new RuneCatalog();
        var red = Enumerable.Repeat(5001, RunePage.RedSlots).ToArray();
        var blue = Enumerable.Repeat(5031, RunePage.BlueSlots).ToArray();
        var yellow = Enumerable.Repeat(5061, RunePage.YellowSlots).ToArray();
        var black = new[] { 5366, 5367, 5368 };

        var emptyLowLevel = RunePage.Create(catalog, summonerLevel: 1);
        TestAssert.True(emptyLowLevel.IsEmpty);
        TestAssert.Equal(Enumerable.Repeat(0, RunePage.TotalSlots).ToArray(), emptyLowLevel.WireIds.ToArray());

        var page = RunePage.Create(catalog, red, blue, yellow, black, summonerLevel: 30);
        red[0] = 5002;
        TestAssert.Equal(5001, page.Red[0]);
        TestAssert.Equal(RunePage.TotalSlots, page.AllIds.Count());
        TestAssert.Equal(page.AllIds.ToArray(), page.WireIds.ToArray());
        TestAssert.Near(9 * (0.524999976158142 + 0.157499998807907 + 0.236399993300438),
            page.Modifiers["FlatPhysicalDamageMod"], 1e-9);
        TestAssert.Throws<ArgumentOutOfRangeException>(() => RunePage.Create(catalog,
            Enumerable.Repeat(5001, RunePage.RedSlots),
            Enumerable.Repeat(5031, RunePage.BlueSlots),
            Enumerable.Repeat(5061, RunePage.YellowSlots), black, summonerLevel: 0));
        var lowLevelMark = RunePage.Create(catalog, new[] { 5001 }, summonerLevel: 1);
        TestAssert.Equal(1, lowLevelMark.Red.Count);
        var tierOneBlue = catalog.All.First(rune => rune.Color == RuneColor.Blue && rune.Tier == RuneTier.One);
        TestAssert.Throws<ArgumentException>(() => RunePage.Create(catalog,
            blue: new[] { tierOneBlue.Id }, summonerLevel: 1));
        TestAssert.Throws<ArgumentException>(() => RunePage.Create(catalog,
            Enumerable.Repeat(5031, RunePage.RedSlots), blue, yellow, black));

        var capacities = new[]
        {
            (Level: 1, Red: 1, Yellow: 0, Blue: 0, Black: 0),
            (Level: 2, Red: 1, Yellow: 1, Blue: 0, Black: 0),
            (Level: 3, Red: 1, Yellow: 1, Blue: 1, Black: 0),
            (Level: 9, Red: 3, Yellow: 3, Blue: 3, Black: 0),
            (Level: 10, Red: 3, Yellow: 3, Blue: 3, Black: 1),
            (Level: 11, Red: 4, Yellow: 3, Blue: 3, Black: 1),
            (Level: 19, Red: 6, Yellow: 6, Blue: 6, Black: 1),
            (Level: 20, Red: 6, Yellow: 6, Blue: 6, Black: 2),
            (Level: 21, Red: 7, Yellow: 6, Blue: 6, Black: 2),
            (Level: 29, Red: 9, Yellow: 9, Blue: 9, Black: 2),
            (Level: 30, Red: 9, Yellow: 9, Blue: 9, Black: 3),
        };
        foreach (var expected in capacities)
        {
            TestAssert.Equal(expected.Red, RunePage.UnlockedSlots(expected.Level, RuneColor.Red));
            TestAssert.Equal(expected.Yellow, RunePage.UnlockedSlots(expected.Level, RuneColor.Yellow));
            TestAssert.Equal(expected.Blue, RunePage.UnlockedSlots(expected.Level, RuneColor.Blue));
            TestAssert.Equal(expected.Black, RunePage.UnlockedSlots(expected.Level, RuneColor.Black));
        }
        TestAssert.Equal(RuneTier.One, RunePage.MaxUsableTier(1));
        TestAssert.Equal(RuneTier.One, RunePage.MaxUsableTier(9));
        TestAssert.Equal(RuneTier.Two, RunePage.MaxUsableTier(10));
        TestAssert.Equal(RuneTier.Two, RunePage.MaxUsableTier(19));
        TestAssert.Equal(RuneTier.Three, RunePage.MaxUsableTier(20));
        TestAssert.Throws<ArgumentOutOfRangeException>(() =>
            RunePage.UnlockedSlots(1, (RuneColor)99));

        RunePage OneRune(RuneData rune, int level) => rune.Color switch
        {
            RuneColor.Red => RunePage.Create(catalog, red: new[] { rune.Id }, summonerLevel: level),
            RuneColor.Yellow => RunePage.Create(catalog, yellow: new[] { rune.Id }, summonerLevel: level),
            RuneColor.Blue => RunePage.Create(catalog, blue: new[] { rune.Id }, summonerLevel: level),
            RuneColor.Black => RunePage.Create(catalog, black: new[] { rune.Id }, summonerLevel: level),
            _ => throw new ArgumentOutOfRangeException(),
        };
        var tierTwo = catalog.All.First(rune => rune.Tier == RuneTier.Two);
        // RunePage.Create has no tier gate (the shipped client has none; bots and the launcher use it). The tier gate is the platform's
        // (owner decision 18 October 2026, evening): PlatformAccounts refuses an over-tier rune purchase and page save, see
        // server.air-rune-tier-two-needs-level-ten and its siblings in AirRuneTierChecks.
        _ = OneRune(tierTwo, 9);
        _ = OneRune(tierTwo, 10);
        _ = OneRune(catalog.All.First(rune => rune.Tier == RuneTier.Three), 20);
        _ = OneRune(catalog.All.First(rune => rune.Tier == RuneTier.One), 20);
    }

    // The event runes (8001-8034) and the Razer runes (10001-10002) are ordinary rune records in DATA/Items. They resolve for page
    // validation and stat aggregation but stay out of All/Count, which feed the store, the combiner and the full-inventory grant.
    [Check("data.runes.event-and-promotional-records-resolve-without-being-granted")]
    public static void EventAndPromotionalRunesResolveWithoutJoiningTheGrantList()
    {
        var catalog = new RuneCatalog();
        TestAssert.Equal(374, catalog.Count);
        TestAssert.Equal(36, catalog.Special.Count);
        TestAssert.False(catalog.All.Any(rune => rune.Id >= 8000));
        var candyCane = catalog.Get(8001)!;
        TestAssert.Equal("Mark of the Crippling Candy Cane", candyCane.DisplayName);
        TestAssert.Equal(RuneColor.Red, candyCane.Color);
        TestAssert.Equal(RuneTier.Two, candyCane.Tier);
        TestAssert.Near(0.01983500085771084, candyCane.Modifier("FlatCritDamageMod"), 1e-12);
        TestAssert.Equal("Razer Mark of Precision", catalog.Get(10001)!.DisplayName);
        TestAssert.True(catalog.Contains(10002));
        TestAssert.Near(0.01983500085771084, catalog.AggregateModifiers([8001])["FlatCritDamageMod"], 1e-12);
    }

    [Check("core-runes.bot-page-keeps-every-listed-rune")]
    public static void BotPageIsNotBoundByLobbyRules()
    {
        var paths = new RuneCatalog().Paths;
        var catalog = new RuneCatalog(paths);
        // Annie eINTERMEDIATE lists ten yellow runes; Shen eUBER lists eighteen red ones. Neither fits a lobby page.
        foreach (var (champion, difficulty, count) in new[] { ("Annie", 1, 22), ("Shen", 3, 30) })
        {
            var ids = BotLoadoutReader.Read(paths, champion, difficulty)!.Runes;
            TestAssert.Equal(count, ids.Count);
            TestAssert.Throws<ArgumentException>(() => RunePage.Create(catalog,
                ids.Where(id => catalog.Get(id)!.Color == RuneColor.Red),
                ids.Where(id => catalog.Get(id)!.Color == RuneColor.Blue),
                ids.Where(id => catalog.Get(id)!.Color == RuneColor.Yellow),
                ids.Where(id => catalog.Get(id)!.Color == RuneColor.Black)));
            var page = RunePage.CreateBot(catalog, ids);
            TestAssert.Equal(count, page.AllIds.Count());
            TestAssert.Equal(ids.ToArray(), page.WireIds.Take(count).ToArray());
            var expected = catalog.AggregateModifiers(ids);
            TestAssert.True(expected.Count > 0);
            TestAssert.Equal(expected.Count, page.Modifiers.Count);
            foreach (var (name, value) in expected) TestAssert.Near(value, page.Modifiers[name], 1e-9);
        }
        // A bot page never throws (Reconstruction): an id without a rune record reads as an empty slot, the 31st entry is ignored.
        var unknown = RunePage.CreateBot(catalog, new[] { 5001, 4999, 0, 5001, -7 });
        TestAssert.Equal(2, unknown.AllIds.Count());
        TestAssert.Equal(new[] { 5001, 0, 0, 5001, 0 }, unknown.WireIds.Take(5).ToArray());
        TestAssert.Equal(RunePage.TotalSlots, RunePage.CreateBot(catalog,
            Enumerable.Repeat(5001, RunePage.TotalSlots + 1)).AllIds.Count());
        TestAssert.True(RunePage.CreateBot(catalog, new[] { 4999 }).IsEmpty);
        TestAssert.True(RunePage.CreateBot(catalog, Array.Empty<int>()).IsEmpty);
        // The headless match seats its bots as ordinary participants (IsBot false): Shen's eUBER page, 18 marks for 9 slots,
        // must pass the definition check because the page itself is a bot page.
        var shenPage = RunePage.CreateBot(catalog, BotLoadoutReader.Read(paths, "Shen", 3)!.Runes);
        TestAssert.True(shenPage.IsBotList);
        new Amberline.Network.Sessions.SessionParticipantDefinition(1, 0, "Shen", "Shen", Team.Order, Runes: shenPage).Validate();
    }
}
