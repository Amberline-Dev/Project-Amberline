using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Server;
using Amberline.Simulation.Bots;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Champions;
using Amberline.Tools.Audits;
using Amberline.Tools.Diagnostics;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Tests.Spells;

/// <summary>
/// <c>ChampionCatalog</c> is the one champion list. These checks keep it honest against the shipped files,
/// the registered champion modules and everything a champion needs to be selectable in AIR.
/// </summary>
public static class ChampionCatalogChecks
{
    [Check("spells-champion-catalog-matches-shipped-data")]
    public static void CatalogMatchesTheShippedClientAndAirData()
    {
        var all = ChampionCatalog.All;
        TestAssert.Equal(84, all.Count, "patch 126 AIR ships 84 champions");
        TestAssert.Equal(84, all.Select(champion => champion.Id).Distinct().Count());
        TestAssert.Equal(84, all.Select(champion => champion.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        TestAssert.Equal(all.Select(champion => champion.Id).Order().ToArray(), all.Select(champion => champion.Id).ToArray(),
            "the catalog is ordered by id");

        var data = GameData.Default;
        foreach (var champion in all)
        {
            // The name is the DATA/Characters folder, so every champion resolves to shipped data.
            TestAssert.True(File.Exists(data.Paths.Data($"Characters/{champion.Name}/{champion.Name}.inibin")),
                $"{champion.Name}: shipped character inibin");
            TestAssert.True(champion.SkinCount >= 1, $"{champion.Name}: skin count");
            TestAssert.Equal(5, champion.AbilityIcons.Count, $"{champion.Name}: passive + QWER icons");
            TestAssert.True(champion.AbilityIcons.All(icon => icon.Length > 0 && !icon.EndsWith(".dds", StringComparison.Ordinal)),
                $"{champion.Name}: icon names carry no extension");
            // Retail bots are exactly the champions the client names itself (fontconfig game_bot_<Name>).
            TestAssert.Equal(data.ClientNamesBot(champion.Name), champion.RetailBot, $"{champion.Name}: retail bot flag");
            TestAssert.Equal(champion.Role is not null, champion.Playable, $"{champion.Name}: playable <=> role");
            TestAssert.Equal(champion.Playable, champion.DefaultIp is not null, $"{champion.Name}: playable <=> price");
        }
        TestAssert.Equal(18, all.Count(champion => champion.RetailBot), "the 18 game_bot_ champions");
        TestAssert.True(ChampionCatalog.ByName("leesin")?.Name == "LeeSin" && ChampionCatalog.ById(53)?.Name == "Blitzcrank" &&
            ChampionCatalog.ByName("Teemo")?.Id == 17 && ChampionCatalog.ByName("Nobody") is null && ChampionCatalog.ById(999) is null,
            "lookups by name (any case) and id");

        // Independent pins: the ten playable ids and skin counts as the AIR client lists them.
        var pinned = new (string Name, int Id, int Skins)[]
        {
            ("Annie", 1, 6), ("Orianna", 61, 2), ("LeeSin", 64, 2), ("Nidalee", 76, 4), ("Yorick", 83, 2),
            ("Shaco", 35, 4), ("Ezreal", 81, 4), ("Vayne", 67, 3), ("Janna", 40, 2), ("Blitzcrank", 53, 4),
            // Appended by the BuildingBlocks engine M2 (16 October 2026): the first script-driven champion.
            ("Soraka", 16, 2),
            // Appended by the BuildingBlocks engine M3 (16 October 2026).
            ("Taric", 44, 3), ("Kayle", 10, 5), ("Ryze", 13, 6), ("Sivir", 15, 5),
            // Appended by the new-champion phase (19 October 2026, wave 1): the first champion that was never hand-written.
            ("Kassadin", 38, 4),
            // Appended by phase 3, wave 1 (19 October 2026): new champions from their shipped scripts.
            ("Udyr", 77, 2),
            // Appended by phase 3, wave 2 (19 October 2026): Morgana (W33c), then Xin Zhao; the merge of the two had left Morgana out of the pins.
            ("Morgana", 25, 3),
            // Appended by phase 3, wave 2 (19 October 2026): Xin Zhao.
            ("XinZhao", 5, 4),
            // Appended by phase 3, wave 3 (19 October 2026): Amumu (W23b).
            ("Amumu", 32, 6),
            // Wave 3 (19 October 2026): a retail bot, the first of the combat-hook champions.
            ("Alistar", 12, 5),
            // Wave 2 (19 October 2026): Brand (W19f), appended after Alistar as the roster is.
            ("Brand", 63, 3),
            // Appended by phase 3, wave 5 (19 October 2026): Twisted Fate (W24b).
            ("TwistedFate", 4, 5),
        };
        // The pins are the roster as it stood: the development order never changes, a new champion is appended after them. The pinned rows stay
        // exact; whoever is appended later is held to the shipped data below (installed skins = the catalog's skin count + the default).
        TestAssert.Equal(pinned.Select(p => p.Name).ToArray(), ChampionCatalog.PlayableNames.Take(pinned.Length).ToArray(),
            "playable champions keep the development roster order");
        foreach (var appended in ChampionCatalog.Playable.Skip(pinned.Length))
            TestAssert.Equal(appended.SkinCount + 1, data.Skins(appended.Name).Count, appended.Name + " installed skins (default + AIR catalog)");
        foreach (var (name, id, skins) in pinned)
        {
            var champion = ChampionCatalog.ByName(name)!;
            TestAssert.Equal(id, champion.Id, name + " id");
            TestAssert.Equal(skins, champion.SkinCount, name + " skin count");
            TestAssert.Equal(skins + 1, data.Skins(name).Count, name + " installed skins (default + AIR catalog)");
        }
    }

    /// <summary>
    /// Batch 594: the rows are read at runtime from the player's AIR client (AirGeneratedContent.swf) and fontconfig, so no client value
    /// is in the source. This pins what the reader loads by count and hash only: a reader or file change shows here. The hash is of the
    /// rows the generated literal table held until batch 594 (compared field by field when the reader replaced it).
    /// </summary>
    [Check("spells-champion-catalog-runtime-load-hash")]
    public static void TheRuntimeLoadedCatalogMatchesItsPinnedHash()
    {
        var all = ChampionCatalog.All;
        TestAssert.Equal(84, all.Count, "champions read from AirGeneratedContent.swf");
        TestAssert.Equal(312, all.Sum(champion => champion.SkinCount), "skins read from AirGeneratedContent.swf");
        var canonical = string.Join("\n", all.Select(champion => string.Join("|", champion.Id, champion.Name, champion.SkinCount,
            string.Join(",", champion.SearchTags), string.Join(",", champion.AbilityIcons), champion.RetailBot ? 1 : 0)));
        TestAssert.Equal(ExpectedCatalogHash,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))),
            "SHA-256 of the loaded rows (id|name|skins|tags|icons|bot, by id)");
    }

    private const string ExpectedCatalogHash = "6d7ce3cee79ff4f3e230044e522a5c1008164f2608d6c320f5737517ff05e5fe";

    [Check("spells-champion-catalog-playable-equals-modules")]
    public static void PlayableSetEqualsRegisteredModulesAndEverySelectabilityInput()
    {
        var playable = ChampionCatalog.Playable;
        var names = playable.Select(champion => champion.Name).Order(StringComparer.Ordinal).ToArray();

        // Supported = registered module: the explicit dispatcher list in ProductionMatchComposition is the other half.
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, "Annie", "Shaco");
        var dispatcher = (ChampionSpellDispatcher)runtime.Dependencies.ChampionDispatcher;
        TestAssert.Equal(names, dispatcher.RegisteredChampionNames.Order(StringComparer.Ordinal).ToArray(),
            "the playable champions are exactly the registered modules");
        foreach (var champion in ChampionCatalog.All)
            TestAssert.Equal(champion.Playable, dispatcher.FindModule(champion.Name) is not null,
                $"{champion.Name}: playable <=> registered module");

        // Every list the server derives from the catalog agrees with it.
        void Same(IEnumerable<string> actual, string what) =>
            TestAssert.Equal(names, actual.Order(StringComparer.Ordinal).ToArray(), what);
        Same(ServerOptions.SupportedChampions, "ServerOptions.SupportedChampions");
        Same(AirGameDtos.ChampionNames.Values, "AirGameDtos.ChampionNames");
        Same(BotController.SupportedChampions, "BotController.SupportedChampions");
        Same(DeathRecapOrigin.ChampionNames, "DeathRecapOrigin.ChampionNames");
        // The default headless match seats ten champions (five per side): the first ten playable ones, the hand-written ten.
        TestAssert.Equal(10, HeadlessMatch.DefaultRoster.Count, "HeadlessMatch.DefaultRoster seats ten champions");
        TestAssert.Equal(ChampionCatalog.PlayableNames.Take(10).ToArray(), HeadlessMatch.DefaultRoster.ToArray(),
            "HeadlessMatch.DefaultRoster is the first ten playable champions");
        Same(ChampionRosterAudit.Inventory().Champions.Select(row => row.Name), "ChampionRosterAudit");

        // Batch 65 (owner decision): AIR lists every catalog champion (84); an unplayable one is locked (owned and bot flags false)
        // and a playable one is owned and bot-enabled. The lists below (economy id, store, skins, bots) stay playable-only.
        var air = ((ArrayCollection)AirDtos.Champions()).Cast<AsObject>().ToArray();
        TestAssert.Equal(ChampionCatalog.All.Select(champion => (double)champion.Id).Order().ToArray(),
            air.Select(row => Convert.ToDouble(row["championId"])).Order().ToArray(), "AIR champion list");
        foreach (var row in air)
        {
            var champion = ChampionCatalog.All.Single(candidate => candidate.Id == (int)Convert.ToDouble(row["championId"]));
            TestAssert.Equal(champion.Playable, Convert.ToBoolean(row["owned"]), $"{champion.Name}: AIR grid owned <=> playable");
            TestAssert.Equal(champion.Playable, Convert.ToBoolean(row["botEnabled"]), $"{champion.Name}: AIR grid bot flag <=> playable");
        }
        var artRoot = Path.Combine(Path.GetTempPath(), "league-catalog-art-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Store art exists for every champion id, as in the staged AIR client; only the catalog may decide who is offered.
            var championArt = Path.Combine(artRoot, "assets", "storeImages", "content", "champions");
            Directory.CreateDirectory(championArt);
            foreach (var champion in ChampionCatalog.All)
                File.WriteAllBytes(Path.Combine(championArt, $"{champion.Id}_Web_0.jpg"), [0xff, 0xd8, 0xff, 0xd9]);
            var store = new PlatformStoreCatalog(new PlatformEconomyRules(), artRoot);
            foreach (var champion in ChampionCatalog.All)
            {
                var offered = store.TryGetStockChampion(champion.Id, out var item);
                // Owner decision of 18 October 2026: the store lists all 84 champions at their retail price; the ones the server cannot play are
                // Locked and PlatformAccounts.Purchase refuses them (the same rule as the AIR grid above).
                TestAssert.True(offered, $"{champion.Name}: the store lists every champion");
                TestAssert.Equal(!champion.Playable, item.Locked, $"{champion.Name}: the store locks the champions the server cannot play");
                TestAssert.Equal(champion.Playable, PlatformBots.IsBotEnabled(champion.Id), $"{champion.Name}: lobby bot");
                TestAssert.Equal(champion.Playable ? champion.Id : 0, PlatformEconomy.ChampionId(champion.Name),
                    $"{champion.Name}: economy id");
                if (!champion.Playable) continue;
                TestAssert.Equal($"content/champions/{champion.Id}_Web_0.jpg", item.ImagePath, $"{champion.Name}: store art");
                // Batch 597: the 2011 price list is operator content; the neutral list prices every champion alike.
                if (PlatformContent.Has("store/prices.json")) TestAssert.Equal(champion.DefaultIp, item.IpPrice, $"{champion.Name}: store price");
                TestAssert.Equal(champion.Name, AirGameDtos.ChampionNames[champion.Id], $"{champion.Name}: AIR id");
                var skins = AirGameDtos.SkinIds(champion.Id, skinsOff: false).ToArray();
                TestAssert.True(skins.Length >= 1 && skins.All(skin => skin > champion.Id * 1000 && skin <= champion.Id * 1000 + champion.SkinCount),
                    $"{champion.Name}: AIR skins");
                TestAssert.True(store.ChampionContent(champion.Id) is { Abilities.Count: 5 }, $"{champion.Name}: store content");
                TestAssert.True(PlatformStoreCatalog.ChampionSearchTags(champion.Id).SequenceEqual(champion.SearchTags),
                    $"{champion.Name}: search tags");
            }
        }
        finally { if (Directory.Exists(artRoot)) Directory.Delete(artRoot, recursive: true); }

        // The staged AIR install, when this checkout sits beside it, carries the store art of every playable champion.
        var staged = Path.GetFullPath(Path.Combine(GameData.Default.Paths.Root, "..", "..", "..", "..", "..",
            "AIR126-PREVIEW", "air", "assets", "storeImages", "content", "champions"));
        if (Directory.Exists(staged))
            foreach (var champion in playable)
                TestAssert.True(File.Exists(Path.Combine(staged, $"{champion.Id}_Web_0.jpg")),
                    $"{champion.Name}: staged AIR store art");
    }
}
