using Amberline.Core;
using Amberline.Data;
using Amberline.Host;
using Amberline.Platform;

namespace Amberline.Tests.Air;

public static class AirPublicRegistrationChecks
{
    [Check("server.air-public-registration-ownership-policy")]
    public static void PublicRegistrationKeepsFreshAndMaxedOwnershipRules()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-public-registration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var accounts = new PlatformAccounts(root);
            var maxed = accounts.RegisterPublic("PublicMaxed", "testing-password", "Maxed", maxed: true);
            var maxedState = PlatformEconomy.For(maxed);
            TestAssert.True(maxedState.AllUnlocked, "the host default preserves the existing fully unlocked account policy");
            TestAssert.Equal(30, maxed.Loadout.SummonerLevel);
            TestAssert.True(PlatformEconomy.CanPlay(maxed, 61, accounts.EconomyRules));

            var fresh = accounts.RegisterPublic("public_fresh", "testing-password", "Fresh", maxed: false);
            var freshState = PlatformEconomy.For(fresh);
            TestAssert.False(freshState.AllUnlocked);
            TestAssert.Equal(1, fresh.Loadout.SummonerLevel);
            TestAssert.Equal(0L, freshState.Ip);
            TestAssert.Equal(0L, freshState.Rp);
            foreach (var freeChampionId in accounts.EconomyRules.FreeChampionIds)
                TestAssert.True(PlatformEconomy.CanPlay(fresh, freeChampionId, accounts.EconomyRules),
                    "the configured free champion pool is playable by a fresh account");
            TestAssert.Equal("SummonerHeal", fresh.Loadout.SummonerSpell1);
            TestAssert.Equal("SummonerHaste", fresh.Loadout.SummonerSpell2);
            TestAssert.Throws<ArgumentException>(() => accounts.SaveLoadout(fresh.Id,
                fresh.Loadout with { SummonerSpell1 = "SummonerFlash" }),
                "a fresh account cannot save Flash before its level requirement");
            TestAssert.Equal("SummonerFlash", maxed.Loadout.SummonerSpell1);
            accounts.SaveLoadout(maxed.Id, maxed.Loadout with { SummonerSpell1 = "SummonerFlash" });

            var manager = new GameManager(new HostSettings(GameData.Default.Paths.Root, ServerExecutable(), BindAddress: "127.0.0.1"),
                Path.Combine(root, "games"));
            try
            {
                var games = new PlatformGames(accounts, manager, "127.0.0.1");
                var created = (RtmpSharp.IO.AsObject)games.Create(
                    fresh, "Public registration", "", 1, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE");
                var lobbyId = Convert.ToInt32(created["id"]);
                var revision = Convert.ToInt32(created["optimisticLock"]);
                games.Start(fresh.Id, lobbyId, revision);
                TestAssert.True(games.Pick(fresh.Id, accounts.EconomyRules.FreeChampionIds[0]) is not null);
                TestAssert.Throws<ArgumentException>(() => games.SelectSpells(fresh.Id,
                    AirGameDtos.SpellId("SummonerFlash"), AirGameDtos.SpellId("SummonerHaste")),
                    "champion selection cannot equip Flash before level twelve");
            }
            finally
            {
                manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            TestAssert.Throws<ArgumentException>(() => accounts.RegisterPublic(
                "PUBLICMAXED", "another-password", "Another Summoner"),
                "public registration must reject duplicate usernames case-insensitively");
            TestAssert.Throws<ArgumentException>(() => accounts.RegisterPublic(
                "another_user", "another-password", "maxed"),
                "public registration must reject duplicate summoner names case-insensitively");

            var rune = accounts.StoreCatalog.Items.FirstOrDefault(item =>
                item.Kind == "RUNE" && item.MinimumLevel <= fresh.Loadout.SummonerLevel && item.IpPrice is > 0);
            TestAssert.True(rune is not null, "the shipped catalog must expose at least one level-one rune");
            var runePrice = rune!.IpPrice!.Value;
            var freeChampion = accounts.EconomyRules.FreeChampionIds[0];
            var championPrice = accounts.StoreCatalog.GetChampion(freeChampion).IpPrice!.Value;
            accounts.GrantCurrency(fresh.Id, checked(championPrice + runePrice * 2), 0, Guid.NewGuid());
            accounts.Purchase(fresh.Id, $"champion:{freeChampion}", "IP", Guid.NewGuid());
            TestAssert.True(PlatformEconomy.For(accounts.Get(fresh.Id)).OwnedChampions!.Contains(freeChampion),
                "free champions remain purchasable and become explicit ownership after purchase");
            TestAssert.Throws<InvalidOperationException>(() => accounts.Purchase(
                fresh.Id, $"champion:{freeChampion}", "IP", Guid.NewGuid()),
                "a purchased champion cannot be bought twice");
            accounts.Purchase(fresh.Id, rune.Id, "IP", Guid.NewGuid());
            accounts.Purchase(fresh.Id, rune.Id, "IP", Guid.NewGuid());
            var afterRunes = PlatformEconomy.For(accounts.Get(fresh.Id));
            TestAssert.Equal(2, afterRunes.OwnedRunes![rune.ContentId],
                "independent purchases preserve duplicate rune ownership up to the shipped quantity limit");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string ServerExecutable() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "Amberline.Server", "release", "Amberline.Server.exe"));
}
