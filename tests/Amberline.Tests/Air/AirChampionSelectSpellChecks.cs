using Amberline.Core;
using Amberline.Platform;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;
using static Amberline.Tests.Air.AirQueueChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 727: the summoner spells a champion-select seat is sent. Every spell a seat holds must be on its map's list
/// (<see cref="AirGameDtos.SpellNamesFor"/>) and unlocked at its summoner's level (<see cref="AirDtos.SpellMinimumLevel"/>, the shipped
/// SpellGeneratedData minLevel). Owner report, 9 November 2026: the champion-select Spells box showed two empty slots.
/// </summary>
public static class AirChampionSelectSpellChecks
{
    [Check("server.air-champion-select-low-level-dominion-seat-gets-usable-spells")]
    public static void LowLevelDominionSeatGetsUsableSpells() => LowLevelDominionSeatAsync().GetAwaiter().GetResult();

    [Check("server.air-champion-select-maxed-dominion-seat-keeps-flash-and-dot")]
    public static void MaxedDominionSeatKeepsFlashAndDot() => MaxedDominionSeatAsync().GetAwaiter().GetResult();

    [Check("server.air-champion-select-classic-seats-keep-the-saved-pair")]
    public static void ClassicSeatsKeepTheSavedPair() => ClassicSeatsAsync().GetAwaiter().GetResult();

    [Check("server.air-champion-select-queued-dominion-seat-gets-usable-spells")]
    public static void QueuedDominionSeatGetsUsableSpells() => QueuedDominionSeatAsync().GetAwaiter().GetResult();

    [Check("server.air-champion-select-dominion-pick-refuses-a-spell-its-level-locks")]
    public static void DominionPickRefusesALockedSpell() => DominionPickAsync().GetAwaiter().GetResult();

    private static async Task LowLevelDominionSeatAsync()
    {
        var root = TemporaryDirectory("league-air-champion-select-low-odin");
        try
        {
            await using var rig = new AirPartyQueueChecks.Rig(root);
            var host = rig.SignIn("spellodinhost");
            var economy = rig.SignIn("spellodineconomy", maxed: false);
            // Teleport is a level-one spell on the classic lists, but the Crystal Scar does not offer it.
            SaveSpells(rig, economy, "SummonerTeleport", "SummonerHeal");
            CreateAndJoin(rig, host, economy, GameMaps.CrystalScar, 10, GameMaps.DominionMode);
            var seat = SeatSpells(rig, economy.Id);
            AssertUsable(seat.First, GameMaps.CrystalScar, 1, "the first spell");
            AssertUsable(seat.Second, GameMaps.CrystalScar, 1, "the second spell");
            // The seat takes the economy default (Heal, Haste: unlocked at level one, offered on the Crystal Scar), not Flash and Dot.
            TestAssert.Equal(AirGameDtos.SpellId("SummonerHeal"), seat.First, "a level-one seat on the Crystal Scar gets Heal");
            TestAssert.Equal(AirGameDtos.SpellId("SummonerHaste"), seat.Second, "a level-one seat on the Crystal Scar gets Haste");
            // The saved loadout itself is unchanged (batch 500): the default is for this game only.
            TestAssert.Equal("SummonerTeleport", rig.Accounts.Get(economy.Id).Loadout.SummonerSpell1,
                "the seat's default does not overwrite the saved loadout");
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task MaxedDominionSeatAsync()
    {
        var root = TemporaryDirectory("league-air-champion-select-maxed-odin");
        try
        {
            await using var rig = new AirPartyQueueChecks.Rig(root);
            var host = rig.SignIn("spellmaxodinhost");
            SaveSpells(rig, host, "SummonerTeleport", "SummonerHeal");
            rig.Games.Create(rig.Accounts.Get(host.Id), "Spells maxed", "", GameMaps.CrystalScar, 10,
                AirGameDtos.BlindPickConfigId, GameMaps.DominionMode, "NONE");
            var seat = SeatSpells(rig, host.Id);
            // A full summoner (level 30) keeps the platform default, Flash and Dot: both on the Crystal Scar and unlocked.
            TestAssert.Equal(AirGameDtos.SpellId("SummonerFlash"), seat.First, "a level-30 seat on the Crystal Scar keeps Flash");
            TestAssert.Equal(AirGameDtos.SpellId("SummonerDot"), seat.Second, "a level-30 seat on the Crystal Scar keeps Dot");
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task ClassicSeatsAsync()
    {
        var root = TemporaryDirectory("league-air-champion-select-classic");
        try
        {
            await using var rig = new AirPartyQueueChecks.Rig(root);
            // Summoner's Rift and Twisted Treeline are CLASSIC: every classic spell is offered, so a level-one seat keeps its pair.
            foreach (var (mapId, capacity) in new[] { (GameMaps.Normal, 10), (GameMaps.TwistedTreeline, 6) })
            {
                var host = rig.SignIn($"spellclassichost{mapId}");
                var economy = rig.SignIn($"spellclassic{mapId}", maxed: false);
                SaveSpells(rig, economy, "SummonerTeleport", "SummonerHeal");
                CreateAndJoin(rig, host, economy, mapId, capacity, GameMaps.ClassicMode);
                var seat = SeatSpells(rig, economy.Id);
                TestAssert.Equal(AirGameDtos.SpellId("SummonerTeleport"), seat.First, $"map {mapId} keeps the saved Teleport");
                TestAssert.Equal(AirGameDtos.SpellId("SummonerHeal"), seat.Second, $"map {mapId} keeps the saved Heal");
            }
        }
        finally { DeleteDirectory(root); }
    }

    /// <summary>The matchmade path: ten players queue for the Crystal Scar, and the match's seat takes the same default as a custom game's.</summary>
    private static async Task QueuedDominionSeatAsync()
    {
        var root = TemporaryDirectory("league-air-champion-select-queue-odin");
        try
        {
            await using var rig = new AirPartyQueueChecks.Rig(root);
            var economy = rig.SignIn("spellqueueeconomy", maxed: false);
            SaveSpells(rig, economy, "SummonerTeleport", "SummonerHeal");
            var others = Enumerable.Range(0, 9).Select(index => rig.SignIn("spellqueuemate" + index)).ToArray();
            rig.Queue(economy, PlatformGames.DominionQueueId);
            foreach (var player in others) rig.Queue(player, PlatformGames.DominionQueueId);
            rig.Games.Tick();
            var seat = SeatSpells(rig, economy.Id);
            AssertUsable(seat.First, GameMaps.CrystalScar, 1, "the first spell");
            AssertUsable(seat.Second, GameMaps.CrystalScar, 1, "the second spell");
            TestAssert.Equal(AirGameDtos.SpellId("SummonerHeal"), seat.First, "a queued level-one seat on the Crystal Scar gets Heal");
            TestAssert.Equal(AirGameDtos.SpellId("SummonerHaste"), seat.Second, "a queued level-one seat on the Crystal Scar gets Haste");
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task DominionPickAsync()
    {
        var root = TemporaryDirectory("league-air-champion-select-odin-pick");
        try
        {
            await using var rig = new AirPartyQueueChecks.Rig(root);
            var host = rig.SignIn("spellodinpickhost");
            var economy = rig.SignIn("spellodinpickeconomy", maxed: false);
            CreateAndJoin(rig, host, economy, GameMaps.CrystalScar, 10, GameMaps.DominionMode);
            var created = (AsObject)rig.Games.Current(host.Id)!;
            rig.Games.Start(host.Id, Convert.ToInt32(created["id"]), Convert.ToInt64(created["optimisticLock"]));
            var before = SeatSpells(rig, economy.Id);
            // Garrison unlocks at level four, so a level-one summoner cannot pick it on the Crystal Scar either (the classic pick is refused the same way).
            TestAssert.Throws<ArgumentException>(() => rig.Games.SelectSpells(economy.Id,
                AirGameDtos.SpellId("SummonerOdinGarrison"), AirGameDtos.SpellId("SummonerHeal")),
                "a level-one summoner cannot pick Garrison, which unlocks at level four");
            TestAssert.Equal(before, SeatSpells(rig, economy.Id), "a refused pick changes nothing");
            // Promote unlocks at level one: the Crystal Scar pick is accepted.
            rig.Games.SelectSpells(economy.Id, AirGameDtos.SpellId("SummonerOdinPromote"), AirGameDtos.SpellId("SummonerHeal"));
            var seat = SeatSpells(rig, economy.Id);
            TestAssert.Equal(AirGameDtos.SpellId("SummonerOdinPromote"), seat.First, "a level-one summoner may pick Promote");
            TestAssert.Equal(AirGameDtos.SpellId("SummonerHeal"), seat.Second, "the pick keeps Heal");
        }
        finally { DeleteDirectory(root); }
    }

    /// <summary>Saves the account's spells as its loadout (what the champion-select spell pick keeps for the next game).</summary>
    private static void SaveSpells(AirPartyQueueChecks.Rig rig, PlatformAccount account, string first, string second) =>
        rig.Accounts.SaveLoadout(account.Id, rig.Accounts.Get(account.Id).Loadout with { SummonerSpell1 = first, SummonerSpell2 = second });

    /// <summary>The host opens a custom game on the map, and the seat joins it with its fresh account (the platform reads the saved loadout).</summary>
    private static void CreateAndJoin(AirPartyQueueChecks.Rig rig, PlatformAccount host, PlatformAccount seat, int mapId, int capacity, string mode)
    {
        var created = (AsObject)rig.Games.Create(rig.Accounts.Get(host.Id), $"Spells {mapId} {seat.Summoner}", "", mapId, capacity,
            AirGameDtos.BlindPickConfigId, mode, "NONE");
        rig.Games.Join(rig.Accounts.Get(seat.Id), Convert.ToInt32(created["id"]), "");
    }

    /// <summary>The spells the game DTO sends for one account: accountSpellsMap is flattened, [accountId, spell1, spell2] per member.</summary>
    private static (int First, int Second) SeatSpells(AirPartyQueueChecks.Rig rig, long account)
    {
        var game = (AsObject)rig.Games.Current(account)!;
        var flat = ((ArrayCollection)game["accountSpellsMap"]).Cast<object>().Select(value => (long)(double)value).ToArray();
        for (var index = 0; index + 2 < flat.Length; index += 3)
            if (flat[index] == account) return ((int)flat[index + 1], (int)flat[index + 2]);
        throw new InvalidOperationException($"the game DTO sends no spells for account {account}");
    }

    /// <summary>A spell a seat holds is on its map's list and unlocked at the seat's summoner level.</summary>
    private static void AssertUsable(int spell, int mapId, int level, string which)
    {
        TestAssert.True(AirGameDtos.SpellNamesFor(mapId).ContainsKey(spell), $"{which} ({spell}) is on map {mapId}'s list");
        var name = AirGameDtos.AllSpellNames[spell];
        TestAssert.True(AirDtos.SpellMinimumLevel(name) <= level,
            $"{which} ({name}) needs summoner level {AirDtos.SpellMinimumLevel(name)}; the seat is level {level}");
    }
}
