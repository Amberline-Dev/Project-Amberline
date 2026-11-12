using Amberline.Core;
using Amberline.Data;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;
using static Amberline.Tests.Air.AirQueueChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 751: a champion the host turns off in Settings (<see cref="PlatformControls.IsEnabled"/>) is greyed out in the client's grid and
/// refused wherever it is newly chosen: a pick, a lobby bot, the Co-op bots, a saved loadout and the store. A pick or a bot already in a
/// champion select stands and launches; the tutorials keep their own champions.
/// </summary>
public static class AirDisabledChampionChecks
{
    private const string ChampionOff = "This champion is turned off on this server.";
    private const string TooFewBots = "This server has too few bot champions turned on for this queue.";

    [Check("server.air-disabled-champion-refused")]
    public static void DisabledChampionIsRefused() => DisabledChampionAsync().GetAwaiter().GetResult();

    private static async Task DisabledChampionAsync()
    {
        var root = TemporaryDirectory("air-disabled-champion");
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"), root);
            await using var manager = NonLaunchingManager(root);
            var clock = new ManualClock();
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock) { Push = (_, _) => { } };
            var sessions = new PlatformSessions(accounts);
            var rpc = new PlatformServices(accounts, sessions, games);
            var player = AirPaidContentChecks.SignIn(accounts, sessions, "disabledplayer", maxed: true);
            AsObject[] Listed() => ((ArrayCollection)rpc.Route(player.Username, "inventoryService", "getAvailableChampions", [])!)
                .Cast<AsObject>().ToArray();
            int[] Inactive() => ((ArrayCollection)((AsObject)((AsObject)rpc.Route(player.Username, "clientFacadeService", "getLoginDataPacketForUser",
                [])!)["clientSystemStates"])["inactiveChampionIdList"]).Cast<object>().Select(Convert.ToInt32).ToArray();

            // With every champion off, Battle Training's three alone stay active, so its fixed grid keeps them (the client checks active first).
            accounts.Controls.DisabledChampions = AirGameDtos.AllChampionNames.Keys.ToHashSet();
            var kept = Listed().Where(c => Equals(c["active"], true)).Select(c => Convert.ToInt32(c["championId"])).ToArray();
            TestAssert.Equal(3, kept.Length, "Battle Training's three stay active");
            TestAssert.Equal(AirGameDtos.AllChampionNames.Count - 3, Inactive().Length, "every other champion is in inactiveChampionIdList");
            TestAssert.False(Inactive().Intersect(kept).Any(), "Battle Training's three are not in inactiveChampionIdList");
            accounts.Controls.DisabledChampions = new HashSet<int>();

            // A retail Co-op bot champion outside the free rotation and Battle Training, so the pool, the store and a fresh account all meet it.
            var disabled = ChampionCatalog.All.First(c => c.RetailBot && PlatformBots.IsBotEnabled(c.Id) &&
                !accounts.EconomyRules.FreeChampionIds.Contains(c.Id) && !kept.Contains(c.Id)).Id;
            var other = ChampionCatalog.All.First(c => PlatformBots.IsBotEnabled(c.Id) && c.Id != disabled).Id;

            // Before the host saves: a lobby holds a bot of the champion and its owner picks it on the other team.
            var lobby = (AsObject)games.Create(accounts.Get(player.Id), "Disabled before", "", GameMaps.Normal, 10,
                AirGameDtos.BlindPickConfigId, GameMaps.ClassicMode, "NONE");
            games.AddBot(player.Id, disabled, Team.Chaos, 0);
            games.Start(player.Id, Convert.ToInt32(lobby["id"]), Convert.ToInt64(((AsObject)games.Current(player.Id)!)["optimisticLock"]));
            games.Pick(player.Id, disabled);

            accounts.Controls.DisabledChampions = new HashSet<int> { disabled };
            var listed = Listed();
            TestAssert.Equal(false, listed.Single(c => Convert.ToInt32(c["championId"]) == disabled)["active"],
                "its ChampionDTO.active is false (the client greys it out)");
            TestAssert.True(listed.Where(c => Convert.ToInt32(c["championId"]) != disabled).All(c => Equals(c["active"], true)),
                "every other ChampionDTO stays active");
            TestAssert.Equal(false, listed.Single(c => Convert.ToInt32(c["championId"]) == disabled)["botEnabled"],
                "its ChampionDTO.botEnabled is false (the lobby's add-bot list leaves it out)");
            TestAssert.Equal(new[] { disabled }, Inactive(), "the login's inactiveChampionIdList names it (the client resets active from this list)");

            // A new pick, a new lobby bot, a saved loadout and a purchase are refused in plain words.
            var picker = AirPaidContentChecks.SignIn(accounts, sessions, "disabledpicker", maxed: true);
            var second = (AsObject)games.Create(accounts.Get(picker.Id), "Disabled after", "", GameMaps.Normal, 10,
                AirGameDtos.BlindPickConfigId, GameMaps.ClassicMode, "NONE");
            games.Start(picker.Id, Convert.ToInt32(second["id"]), Convert.ToInt64(second["optimisticLock"]));
            TestAssert.True(AirPaidContentChecks.Refused(() => games.Pick(picker.Id, disabled), ChampionOff), "a pick of it is refused");
            var owner = AirPaidContentChecks.SignIn(accounts, sessions, "disabledowner", maxed: true);
            games.Create(accounts.Get(owner.Id), "Disabled bots", "", GameMaps.Normal, 10, AirGameDtos.BlindPickConfigId, GameMaps.ClassicMode, "NONE");
            TestAssert.True(AirPaidContentChecks.Refused(() => games.AddBot(owner.Id, disabled, Team.Chaos, 0), ChampionOff), "a bot of it is refused");
            games.AddBot(owner.Id, other, Team.Chaos, 0);
            TestAssert.True(AirPaidContentChecks.Refused(() => accounts.SaveLoadout(picker.Id,
                accounts.Get(picker.Id).Loadout with { Champion = AirGameDtos.ChampionNames[disabled] }), ChampionOff), "saving it in a loadout is refused");
            var buyer = AirPaidContentChecks.SignIn(accounts, sessions, "disabledbuyer", maxed: false);
            accounts.GrantCurrency(buyer.Id, 10_000, 0, Guid.NewGuid());
            TestAssert.True(AirPaidContentChecks.Refused(() => accounts.Purchase(buyer.Id, $"champion:{disabled}", "IP", Guid.NewGuid()), ChampionOff),
                "buying it is refused");
            TestAssert.Equal(10_000L, PlatformEconomy.For(accounts.Get(buyer.Id)).Ip, "a refused purchase takes no IP");
            TestAssert.False(PlatformEconomy.CanPlay(accounts.Get(buyer.Id), disabled, accounts.EconomyRules), "a refused purchase grants nothing");

            // The pick and the bot made before the save stand: lock-in and the countdown launch them.
            games.LockIn(player.Id);
            clock.Now = clock.Now.AddSeconds(PlatformGames.PostPickServerSeconds);
            games.Tick();
            var roster = MatchRoster.Read(Path.Combine(Directory.EnumerateDirectories(Path.Combine(root, "games")).Single(), "roster.json"));
            TestAssert.Equal(2, roster.Count(seat => seat.Champion == AirGameDtos.ChampionNames[disabled]), "the earlier pick and bot reach the game");

            // The Co-op bots never take it, over fifty games (nine of the retail bots per game: without the gate it shows in about half).
            var connections = new Dictionary<string, long>();
            games.AccountForConnection = connection => connections.GetValueOrDefault(connection);
            var coop = accounts.RegisterPublic("disabledcoop", "testing-password", "disabledcoop");
            connections[coop.Username] = coop.Id;
            AsObject Attach(PlatformAccount leader, long[]? team = null, string? invitation = null)
            {
                var answer = (AsObject)games.AttachQueue(leader, leader.Username, [PlatformGames.CoopVsAiQueueId], "EASY", team, invitation);
                games.ConfirmQueueReply(leader.Username);
                return answer;
            }
            int QueueLength() => Convert.ToInt32(((AsObject)games.QueueInfo(PlatformGames.CoopVsAiQueueId))["queueLength"]);
            int[] CoopBots()
            {
                Attach(coop);
                clock.Now = clock.Now.AddSeconds(5);
                games.Tick();
                var game = (AsObject)games.Current(coop.Id)!;
                games.Quit(coop.Id);
                return Bots(game);
            }
            for (var round = 0; round < 50; round++)
            {
                var bots = CoopBots();
                TestAssert.Equal(9, bots.Length, "a solo Co-op game has nine bots");
                TestAssert.False(bots.Contains(disabled), $"game {round}: the Co-op bots do not take it");
            }

            // The boundary: with exactly the nine a solo player needs left, the game starts; with one fewer, the queue refuses in plain words.
            var retail = ChampionCatalog.All.Where(c => c.RetailBot && PlatformBots.IsBotEnabled(c.Id)).Select(c => c.Id).ToArray();
            var nine = retail.Where(id => id != disabled).Take(9).ToArray();
            accounts.Controls.DisabledChampions = retail.Except(nine).ToHashSet();
            var enough = CoopBots();
            TestAssert.True(enough.Length == 9 && enough.All(nine.Contains), "with exactly nine bot champions left a solo player's game starts");
            accounts.Controls.DisabledChampions = retail.Except(nine.Take(8)).ToHashSet();
            var failures = Attach(coop)["playerJoinFailures"] as ArrayCollection;
            TestAssert.True(failures?.Count == 1 && Equals(((AsObject)failures[0])["reasonFailed"], "QUEUE_DISABLED") &&
                Equals(((AsObject)failures[0])["message"], TooFewBots), "with one fewer, the solo player's queue is refused");
            TestAssert.Equal(0, QueueLength(), "the refused player is not queued");

            // A solo player queued before the host turned more off can no longer start, and does not hold back a party that can.
            accounts.Controls.DisabledChampions = retail.Except(nine).ToHashSet();
            TestAssert.True(Attach(coop)["playerJoinFailures"] is null, "nine left: the solo player queues");
            accounts.Controls.DisabledChampions = retail.Except(nine.Take(5)).ToHashSet();
            var party = Enumerable.Range(0, 5).Select(index => accounts.RegisterPublic($"disabledparty{index}", "testing-password", $"disabledparty{index}")).ToArray();
            foreach (var member in party) connections[member.Username] = member.Id;
            foreach (var member in party.Skip(1)) games.AcceptInvite(member, member.Username, "751");
            TestAssert.True(Attach(party[0], party.Select(member => member.SummonerId).ToArray(), "751")["playerJoinFailures"] is null,
                "five left: a party of five, which needs five bots, queues");
            clock.Now = clock.Now.AddSeconds(5);
            games.Tick();
            TestAssert.True(games.Current(party[0].Id) is AsObject partyGame && Bots(partyGame).Length == 5 && Bots(partyGame).All(nine.Take(5).Contains),
                "the party's game starts with the five bots left");
            TestAssert.True(games.Current(coop.Id) is null && QueueLength() == 1, "the earlier solo player waits in the queue");

            // The tutorials keep their champions even with every champion turned off.
            accounts.Controls.DisabledChampions = AirGameDtos.AllChampionNames.Keys.ToHashSet();
            var tutorial = (AsObject)games.CreateTutorial(accounts.RegisterPublic("disabledtutorial", "testing-password", "disabledtutorial"));
            TestAssert.True(Selections(tutorial).Length == 3 && Selections(tutorial).All(id => id != 0), "the Basic Tutorial seats its champion and two bots");
            var late = AirPaidContentChecks.SignIn(accounts, sessions, "disabledlate", maxed: true);
            var normal = (AsObject)games.Create(accounts.Get(late.Id), "Disabled late", "", GameMaps.Normal, 10,
                AirGameDtos.BlindPickConfigId, GameMaps.ClassicMode, "NONE");
            games.Start(late.Id, Convert.ToInt32(normal["id"]), Convert.ToInt64(normal["optimisticLock"]));
            foreach (var id in kept)
                TestAssert.True(AirPaidContentChecks.Refused(() => games.Pick(late.Id, id), ChampionOff), "a normal game refuses Battle Training's three");
            var trainee = accounts.RegisterPublic("disabledtrainee", "testing-password", "disabledtrainee");
            var training = (AsObject)games.CreateBattleTraining(trainee);
            TestAssert.Equal(9, Selections(training).Count(id => id != 0), "Battle Training seats its nine bots");
            foreach (var id in kept)
                TestAssert.True(((ArrayCollection)((AsObject)games.Pick(trainee.Id, id))["playerChampionSelections"]).Cast<AsObject>()
                    .Any(s => Equals(s["summonerInternalName"], "sum" + trainee.SummonerId) && Convert.ToInt32(s["championId"]) == id),
                    "Battle Training still offers each of its three");
            games.LockIn(trainee.Id);
            TestAssert.Equal("POST_CHAMP_SELECT", ((AsObject)games.Current(trainee.Id)!)["gameState"], "Battle Training still locks in");

            accounts.Controls.DisabledChampions = new HashSet<int>();
            TestAssert.Equal(true, Listed().Single(c => Convert.ToInt32(c["championId"]) == disabled)["active"], "on again: it is active");
            accounts.Purchase(buyer.Id, $"champion:{disabled}", "IP", Guid.NewGuid());
            TestAssert.True(PlatformEconomy.CanPlay(accounts.Get(buyer.Id), disabled, accounts.EconomyRules), "on again: it can be bought");
        }
        finally { DeleteDirectory(root); }
    }

    private static int[] Selections(AsObject game) =>
        ((ArrayCollection)game["playerChampionSelections"]).Cast<AsObject>().Select(s => Convert.ToInt32(s["championId"])).ToArray();

    private static int[] Bots(AsObject game) => ((ArrayCollection)game["playerChampionSelections"]).Cast<AsObject>()
        .Where(s => ((string)s["summonerInternalName"]).StartsWith("bot_", StringComparison.Ordinal)).Select(s => Convert.ToInt32(s["championId"])).ToArray();
}
