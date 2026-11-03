using System.Text.Json;
using Amberline.Core;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;
using static Amberline.Tests.Air.AirPartyQueueChecks;
using static Amberline.Tests.Air.AirQueueChecks;

namespace Amberline.Tests.Air;

/// <summary>Batch 586: the ranked queues 4, 6 and 9, their requirements, the Elo rating of each queue, the end-of-game line and the ladder.</summary>
public static class AirRankedChecks
{
    [Check("server.air-ranked-queues-and-requirements")]
    public static void QueuesAndRequirements() => RequirementsAsync().GetAwaiter().GetResult();

    [Check("server.air-ranked-solo-duo-and-teams-match-by-rating")]
    public static void SoloDuoAndTeamsMatchByRating() => MatchingAsync().GetAwaiter().GetResult();

    [Check("server.air-ranked-results-rate-each-queue")]
    public static void ResultsRateEachQueue() => Ratings();

    [Check("server.air-ranked-ladder-lists-ratings")]
    public static void LadderListsRatings() => Ladder();

    [Check("server.air-ranked-solo-3v3-and-dominion-queues")]
    public static void SoloThreeAndDominionQueues() => MoreQueuesAsync().GetAwaiter().GetResult();

    [Check("server.air-ranked-solo-3v3-and-dominion-ratings")]
    public static void SoloThreeAndDominionRatings() => MoreRatings();

    private static async Task MoreQueuesAsync()
    {
        var root = TemporaryDirectory("league-air-ranked-more");
        try
        {
            await using var host = new Rig(root);
            var viewer = host.SignIn("rankmoreview");
            var queues = ((ArrayCollection)host.Rpc.Route(viewer.Username, "matchmakerService", "getAvailableQueues", [])!)
                .Cast<AsObject>().ToDictionary(queue => (double)queue["id"]);
            // Batch 590: the menu's TT solo leaf (11) and Crystal Scar Ranked leaves (18 solo, 19 party).
            foreach (var (id, type, map, mode, size, min, max) in new[]
                     { (11d, "RANKED_SOLO_3x3", 4d, "CLASSIC", 3d, 1, 1), (18d, "ODIN_RANKED_SOLO", 8d, "ODIN", 5d, 1, 1),
                       (19d, "ODIN_RANKED_PREMADE", 8d, "ODIN", 5d, 5, 5) })
            {
                var queue = queues[id];
                TestAssert.Equal(true, queue["ranked"], $"queue {id} is ranked");
                TestAssert.Equal(type, queue["type"]);
                TestAssert.Equal(mode, queue["gameMode"]);
                TestAssert.Equal(30, queue["minLevel"]);
                TestAssert.Equal(min, queue["minimumParticipantListSize"], $"queue {id} minimum party");
                TestAssert.Equal(max, queue["maximumParticipantListSize"], $"queue {id} maximum party");
                TestAssert.Equal(size, queue["numPlayersPerTeam"]);
                TestAssert.Equal(AirGameDtos.DraftConfigId, queue["gameTypeConfigId"]);
                TestAssert.Equal(new object[] { map }, ((ArrayCollection)queue["supportedMapIds"]).Cast<object>().ToArray());
            }
            var login = (AsObject)host.Rpc.Route(viewer.Username, "clientFacadeService", "getLoginDataPacketForUser", [])!;
            var enabled = ((ArrayCollection)((AsObject)login["clientSystemStates"])["enabledQueueIdsList"]).Cast<object>().ToArray();
            TestAssert.True(new object[] { 11d, 18d, 19d }.All(enabled.Contains), "the new ranked queues are enabled");

            // 11 and 18 are solo only (the client moves no TT or Dominion party there); 19 takes a full team.
            var mate = host.SignIn("rankmoremate");
            _ = host.Rpc.Route(mate.Username, "matchmakerService", "acceptInviteForMatchmakingGame", ["800"]);
            foreach (var queue in new[] { 11, 18, 19 })
                TestAssert.Equal("QUEUE_DISABLED", Failure(host.Attach(viewer, "attachTeamToQueue", queue, [viewer.SummonerId, mate.SummonerId], "800")),
                    $"a duo cannot queue {queue}");
            TestAssert.Equal("QUEUE_DISABLED", Failure(host.Attach(viewer, "attachToQueue", 19, null, null)));

            var tt = Enumerable.Range(0, 6).Select(index => host.SignIn("rankmorett" + index)).ToArray();
            foreach (var player in tt) host.Queue(player, PlatformGames.RankedTreelineSoloQueueId);
            var odin = Enumerable.Range(0, 10).Select(index => host.SignIn("rankmoreodin" + index)).ToArray();
            foreach (var player in odin) host.Queue(player, PlatformGames.DominionRankedSoloQueueId);
            var odinTeams = Enumerable.Range(0, 10).Select(index => host.SignIn("rankmoreteam" + index)).ToArray();
            host.Party(odinTeams[0], odinTeams[1..5], PlatformGames.DominionRankedTeamQueueId, "810");
            host.Party(odinTeams[5], odinTeams[6..10], PlatformGames.DominionRankedTeamQueueId, "811");
            host.Games.Tick();

            var three = (AsObject)host.Games.Current(tt[0].Id)!;
            TestAssert.Equal("PRE_CHAMP_SELECT", three["gameState"]);
            TestAssert.Equal(4, three["mapId"]);
            TestAssert.Equal("RANKED_SOLO_3x3", three["queueTypeName"]);
            TestAssert.Equal("RANKED_GAME_SOLO", three["gameType"]);
            TestAssert.Equal(3, Accounts(three, "teamOne").Length);
            var solo = (AsObject)host.Games.Current(odin[0].Id)!;
            TestAssert.Equal(8, solo["mapId"]);
            TestAssert.Equal("ODIN", solo["gameMode"]);
            TestAssert.Equal("ODIN_RANKED_SOLO", solo["queueTypeName"]);
            TestAssert.Equal("PRE_CHAMP_SELECT", solo["gameState"], "Dominion ranked drafts");
            var team = (AsObject)host.Games.Current(odinTeams[0].Id)!;
            TestAssert.Equal("ODIN_RANKED_PREMADE", team["queueTypeName"]);
            TestAssert.Equal("RANKED_GAME_PREMADE", team["gameType"]);
            TestAssert.Equal(odinTeams[..5].Select(player => player.Id).ToArray(), Accounts(team, "teamOne"));

            // Dominion's summoner spells: Garrison (17) in a Dominion ranked game, never Teleport (12); Twisted Treeline the reverse.
            host.Games.SelectSpells(odin[0].Id, 17, 4);
            TestAssert.Throws<ArgumentException>(() => host.Games.SelectSpells(odin[0].Id, 12, 4));
            TestAssert.Throws<ArgumentException>(() => host.Games.SelectSpells(tt[0].Id, 17, 4));
            host.Games.SelectSpells(tt[0].Id, 12, 4);
        }
        finally { DeleteDirectory(root); }
    }

    private static void MoreRatings()
    {
        var root = TemporaryDirectory("league-air-ranked-more-ratings");
        try
        {
            using var accounts = new PlatformAccounts(root);
            var (a, b) = (Register(accounts, "moratea"), Register(accounts, "morateb"));
            // A ranked team 3v3 rating first; ranked solo 3v3 is a summary the client counts as ranked (RankedSolo3x3), so it starts at
            // getInitialRankedRating, and Dominion's (OdinRankedSolo, odinRankedPremade) are not ("Ranked" must lead the name): 1200.
            Ranked(accounts, PlatformGames.RankedTreelineTeamQueueId, 1, [a], [b]);
            TestAssert.Equal(1232, accounts.Rating(a.Id, "RANKED_PREMADE_3x3"));
            Ranked(accounts, PlatformGames.RankedTreelineSoloQueueId, 2, [a], [b]);
            Ranked(accounts, PlatformGames.DominionRankedSoloQueueId, 3, [b], [a]);
            Ranked(accounts, PlatformGames.DominionRankedTeamQueueId, 4, [a], [b]);
            var receipts = accounts.ResultsFor(a.Id);
            TestAssert.Equal(1211, receipts.Single(receipt => receipt.GameId == 2).Ratings![a.Id].Before, "round((1232 + 2400) / 3)");
            TestAssert.Equal(new PlatformRating(1200, 1168), receipts.Single(receipt => receipt.GameId == 3).Ratings![a.Id]);
            TestAssert.Equal(1200, receipts.Single(receipt => receipt.GameId == 4).Ratings![a.Id].Before);

            var summaries = ((ArrayCollection)AirProfileDtos.PlayerStatSummaries(accounts.Get(a.Id), receipts)["playerStatSummarySet"])
                .Cast<AsObject>().ToDictionary(row => (string)row["playerStatSummaryType"]);
            TestAssert.Equal(receipts.Single(receipt => receipt.GameId == 2).Ratings![a.Id].After, summaries["RankedSolo3x3"]["rating"]);
            TestAssert.Equal(1168, summaries["OdinRankedSolo"]["rating"]);
            TestAssert.Equal(1, summaries["OdinRankedSolo"]["losses"]);
            TestAssert.Equal(1232, summaries["odinRankedPremade"]["rating"], "the client's lower-case spelling");
            TestAssert.Equal(0, summaries["OdinUnranked"]["wins"] is int odinWins ? odinWins : -1, "Dominion ranked is not OdinUnranked");
            TestAssert.Equal((1, 0, 0), AirProfileDtos.Record(accounts.Get(a.Id), receipts, "ODIN_RANKED_PREMADE"),
                "the end-of-game counters print the Dominion ranked record");
            TestAssert.Equal((0, 1, 0), AirProfileDtos.Record(accounts.Get(a.Id), receipts, "ODIN_RANKED_SOLO"));

            // No profile row shows them; the ladder does.
            var html = PlatformHome.RenderLadder(accounts);
            foreach (var title in new[] { "Ranked 3x3 Solo", "Dominion Ranked 5x5 Solo", "Dominion Ranked 5x5 Team" })
                TestAssert.True(html.Contains($"<h2>{title}</h2>", StringComparison.Ordinal), title);
            TestAssert.True(html.Contains("<td>1</td><td>moratea</td><td>placement 1/10</td><td>1</td><td>0</td>", StringComparison.Ordinal));
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task RequirementsAsync()
    {
        var root = TemporaryDirectory("league-air-ranked-queues");
        try
        {
            await using var host = new Rig(root);
            var leader = host.SignIn("rankedlead");
            var queues = ((ArrayCollection)host.Rpc.Route(leader.Username, "matchmakerService", "getAvailableQueues", [])!)
                .Cast<AsObject>().ToDictionary(queue => (double)queue["id"]);
            // The menu's Ranked leaves: SR 5v5 solo 4 / party 6, TT 3v3 party 9 (ChooseGameFlowViewLayoutDefinition); the sizes the arranged
            // lobby's Start Game button reads (CreateNormalArrangedGame.as:1201) and the level the menu enables a ranked leaf from.
            foreach (var (id, type, map, size, min, max) in new[]
                     { (4d, "RANKED_SOLO_5x5", 1d, 5d, 1, 2), (6d, "RANKED_PREMADE_5x5", 1d, 5d, 5, 5), (9d, "RANKED_PREMADE_3x3", 4d, 3d, 3, 3) })
            {
                var queue = queues[id];
                TestAssert.Equal(true, queue["ranked"], $"queue {id} is ranked");
                TestAssert.Equal(type, queue["type"]);
                TestAssert.Equal(30, queue["minLevel"], "ranked games need level 30");
                TestAssert.Equal(min, queue["minimumParticipantListSize"], $"queue {id} minimum party");
                TestAssert.Equal(max, queue["maximumParticipantListSize"], $"queue {id} maximum party");
                TestAssert.Equal(size, queue["numPlayersPerTeam"]);
                TestAssert.Equal(AirGameDtos.DraftConfigId, queue["gameTypeConfigId"], "the Ranked leaves are draft_mode");
                TestAssert.Equal(new object[] { map }, ((ArrayCollection)queue["supportedMapIds"]).Cast<object>().ToArray());
            }
            TestAssert.Equal(false, queues[2d]["ranked"]);
            var login = (AsObject)host.Rpc.Route(leader.Username, "clientFacadeService", "getLoginDataPacketForUser", [])!;
            var enabled = ((ArrayCollection)((AsObject)login["clientSystemStates"])["enabledQueueIdsList"]).Cast<object>().ToArray();
            TestAssert.True(new object[] { 4d, 6d, 9d }.All(enabled.Contains), "the ranked queues are enabled");

            // A fresh account fails both requirements; RankedQueueFailuresDialog lists the reasons by summoner name.
            var fresh = host.SignIn("rankedfresh", maxed: false);
            var refused = (ArrayCollection)host.Attach(fresh, "attachToQueue", 4, null, null)["playerJoinFailures"];
            var reasons = refused.Cast<AsObject>().Select(row => (string)row["reasonFailed"]).ToArray();
            TestAssert.Equal(new[] { "RANKED_MIN_LEVEL", "RANKED_NUM_CHAMPS" }, reasons);
            TestAssert.True(refused.Cast<AsObject>().All(row => row.TypeName == "com.riotgames.platform.game.FailedJoinPlayer" &&
                Equals(((AsObject)row["summoner"])["name"], fresh.Summoner)));
            TestAssert.Equal(0, ((AsObject)host.Games.QueueInfo(4))["queueLength"]);

            // A duo with an unqualified member: only the member is named ("Because {0} did not meet the required game play criteria").
            _ = host.Rpc.Route(fresh.Username, "matchmakerService", "acceptInviteForMatchmakingGame", ["600"]);
            var duo = (ArrayCollection)host.Attach(leader, "attachTeamToQueue", 4, [leader.SummonerId, fresh.SummonerId], "600")["playerJoinFailures"];
            TestAssert.Equal(2, duo.Count);
            TestAssert.True(duo.Cast<AsObject>().All(row => Equals(((AsObject)row["summoner"])["name"], fresh.Summoner)));

            // Party sizes: a solo player or a duo never enters the premade queue 6 (the client moves a duo to 4), three never queue 4.
            var others = Enumerable.Range(0, 4).Select(index => host.SignIn("rankedmate" + index)).ToArray();
            TestAssert.Equal("QUEUE_DISABLED", Failure(host.Attach(leader, "attachToQueue", 6, null, null)));
            _ = host.Rpc.Route(others[0].Username, "matchmakerService", "acceptInviteForMatchmakingGame", ["601"]);
            TestAssert.Equal("QUEUE_DISABLED", Failure(host.Attach(leader, "attachTeamToQueue", 6, [leader.SummonerId, others[0].SummonerId], "601")));
            _ = host.Rpc.Route(others[1].Username, "matchmakerService", "acceptInviteForMatchmakingGame", ["601"]);
            TestAssert.Equal("QUEUE_DISABLED", Failure(host.Attach(leader, "attachTeamToQueue", 4,
                [leader.SummonerId, others[0].SummonerId, others[1].SummonerId], "601")));
            TestAssert.True(host.Attach(leader, "attachTeamToQueue", 9,
                [leader.SummonerId, others[0].SummonerId, others[1].SummonerId], "601")["playerJoinFailures"] is null, "three queue 9");
            TestAssert.Equal(3, ((AsObject)host.Games.QueueInfo(9))["queueLength"]);
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task MatchingAsync()
    {
        var root = TemporaryDirectory("league-air-ranked-match");
        try
        {
            await using var host = new Rig(root);
            // A star who beat seven newcomers rates more than the window (150) above the 1200 a new solo/duo player starts at.
            var star = host.SignIn("rankedstar");
            for (var game = 1; game <= 7; game++) Ranked(host.Accounts, 4, game, [star], [host.SignIn("rankedvictim" + game)]);
            var starRating = host.Accounts.Rating(star.Id, "RANKED_SOLO_5x5");
            TestAssert.True(starRating > 1350, $"the star rates {starRating}");
            host.Queue(star, 4);

            // Ten newcomers, a duo among them: they match each other at once; the star keeps waiting.
            var fresh = Enumerable.Range(0, 10).Select(index => host.SignIn("rankednew" + index)).ToArray();
            host.Party(fresh[0], [fresh[1]], 4, "700");
            foreach (var player in fresh.Skip(2)) host.Queue(player, 4);
            host.Games.Tick();
            var game10 = (AsObject)host.Games.Current(fresh[0].Id)!;
            TestAssert.Equal("PRE_CHAMP_SELECT", game10["gameState"], "ranked picks in Draft Mode, bans first");
            TestAssert.Equal("RANKED_GAME_SOLO", game10["gameType"]);
            TestAssert.Equal("RANKED_SOLO_5x5", game10["queueTypeName"]);
            TestAssert.True(((string)game10["name"]).StartsWith("Ranked Solo Game ", StringComparison.Ordinal));
            var one = Accounts(game10, "teamOne");
            TestAssert.True(one.Contains(fresh[0].Id) == one.Contains(fresh[1].Id), "the duo plays on one team");
            TestAssert.True(host.Games.Current(star.Id) is null, "the star is outside the window");
            TestAssert.Equal(1, ((AsObject)host.Games.QueueInfo(4))["queueLength"]);

            // Nine more newcomers: not a game with the star until the window has widened past his rating.
            var late = Enumerable.Range(0, 9).Select(index => host.SignIn("rankedlate" + index)).ToArray();
            foreach (var player in late) host.Queue(player, 4);
            host.Games.Tick();
            TestAssert.True(host.Games.Current(star.Id) is null && host.Games.Current(late[0].Id) is null, "nine and a distant star wait");
            host.Clock.Now = host.Clock.Now.AddSeconds(Math.Ceiling((starRating - 1200 - 150) / 10.0) + 1);
            host.Games.Tick();
            var widened = (AsObject)host.Games.Current(star.Id)!;
            TestAssert.True(Equals(widened["id"], ((AsObject)host.Games.Current(late[0].Id)!)["id"]), "the widened window takes the star");

            // Ranked teams: two parties of five meet in queue 6, each party one team; two parties of three in queue 9 on map 4.
            var teams = Enumerable.Range(0, 10).Select(index => host.SignIn("rankedteam" + index)).ToArray();
            host.Party(teams[0], teams[1..5], 6, "710");
            host.Party(teams[5], teams[6..10], 6, "711");
            var three = Enumerable.Range(0, 6).Select(index => host.SignIn("rankedthree" + index)).ToArray();
            host.Party(three[0], three[1..3], 9, "720");
            host.Party(three[3], three[4..6], 9, "721");
            host.Games.Tick();
            var team5 = (AsObject)host.Games.Current(teams[0].Id)!;
            TestAssert.Equal("RANKED_GAME_PREMADE", team5["gameType"]);
            TestAssert.Equal("RANKED_PREMADE_5x5", team5["queueTypeName"]);
            TestAssert.Equal(teams[..5].Select(player => player.Id).ToArray(), Accounts(team5, "teamOne"));
            TestAssert.Equal(teams[5..].Select(player => player.Id).ToArray(), Accounts(team5, "teamTwo"));
            var team3 = (AsObject)host.Games.Current(three[0].Id)!;
            TestAssert.Equal(4, team3["mapId"]);
            TestAssert.Equal("RANKED_PREMADE_3x3", team3["queueTypeName"]);
            TestAssert.Equal(three[..3].Select(player => player.Id).ToArray(), Accounts(team3, "teamOne"));
        }
        finally { DeleteDirectory(root); }
    }

    private static void Ratings()
    {
        var root = TemporaryDirectory("league-air-ranked-ratings");
        try
        {
            using var accounts = new PlatformAccounts(root);
            var (a, b, c, d) = (Register(accounts, "ratea"), Register(accounts, "rateb"), Register(accounts, "ratec"), Register(accounts, "rated"));

            // Solo/duo: everyone starts at the client's 1200; equal teams, a placement game (K 64): plus or minus 32.
            Ranked(accounts, 4, 1, [a, b], [c, d]);
            var first = accounts.ResultsFor(a.Id).Single();
            TestAssert.Equal(new PlatformRating(1200, 1232), first.Ratings![a.Id]);
            TestAssert.Equal(new PlatformRating(1200, 1168), first.Ratings[d.Id]);

            // The ranked team queue 6 starts each player at the client's getInitialRankedRating, round((best + 2400) / 3); a leaver on the
            // winning team scores a loss.
            Ranked(accounts, 6, 2, [a, b], [c, d], leaver: b);
            var premade = accounts.ResultsFor(a.Id).Single(receipt => receipt.QueueId == 6).Ratings!;
            TestAssert.Equal(1211, premade[a.Id].Before);
            TestAssert.Equal(1189, premade[c.Id].Before);
            var expected = 1 / (1 + Math.Pow(10, (1189 - 1211) / 400.0));
            TestAssert.Equal(1211 + (int)Math.Round(64 * (1 - expected)), premade[a.Id].After);
            TestAssert.Equal(1211 + (int)Math.Round(64 * (0 - expected)), premade[b.Id].After, "the leaver loses");
            TestAssert.Equal(1189 + (int)Math.Round(64 * (0 - (1 - expected))), premade[c.Id].After);

            // The profile: each ranked queue its own summary, rating and record; Unranked untouched.
            var summaries = ((ArrayCollection)AirProfileDtos.PlayerStatSummaries(accounts.Get(a.Id), accounts.ResultsFor(a.Id))
                ["playerStatSummarySet"]).Cast<AsObject>().ToDictionary(row => (string)row["playerStatSummaryType"]);
            TestAssert.Equal(1232, summaries["RankedSolo5x5"]["rating"]);
            TestAssert.Equal(1232, summaries["RankedSolo5x5"]["maxRating"]);
            TestAssert.Equal(1, summaries["RankedSolo5x5"]["wins"]);
            TestAssert.Equal(premade[a.Id].After, summaries["RankedPremade5x5"]["rating"]);
            TestAssert.Equal(0, summaries["RankedPremade3x3"]["rating"], "an unplayed queue stays unrated (the profile shows nothing)");
            TestAssert.Equal(0, summaries["Unranked"]["wins"], "ranked games are not Unranked");
            TestAssert.Equal((1, 0, 0), AirProfileDtos.Record(accounts.Get(b.Id), accounts.ResultsFor(b.Id), "RANKED_SOLO_5x5"));
            TestAssert.Equal((0, 0, 1), AirProfileDtos.Record(accounts.Get(b.Id), accounts.ResultsFor(b.Id), "RANKED_PREMADE_5x5"));

            // The end-of-game screen: ranked, the new rating and its change; recent games read "(ranked game)".
            var game = new PlatformGames.Lobby(1, "Ranked Solo Game 1", "", a.Id, 10)
            {
                QueueId = PlatformGames.RankedSoloQueueId,
                Result = first.Result.Deserialize<MatchResult>(MatchRoster.JsonOptions),
            };
            game.Members.Add(new PlatformGames.Member(accounts.Get(a.Id), Team.Order));
            var results = AirGameDtos.Results(game, game.Members[0], first.Rewards?.GetValueOrDefault(a.Id), accounts.EconomyRules,
                account => AirProfileDtos.Record(account, accounts.ResultsFor(account.Id), "RANKED_SOLO_5x5"), DateTimeOffset.UtcNow,
                first.Ratings[a.Id]);
            TestAssert.Equal(true, results["ranked"]);
            TestAssert.Equal("RANKED_SOLO_5x5", results["queueType"]);
            TestAssert.Equal(1232, results["elo"]);
            TestAssert.Equal(32, results["eloChange"]);
            var mine = (AsObject)((ArrayCollection)results["teamPlayerParticipantStats"]).Cast<AsObject>()
                .Single(row => Equals(row["userId"], (double)a.SummonerId));
            TestAssert.Equal(1d, mine["wins"], "the record counters are the ranked queue's");
            var recent = ((ArrayCollection)AirGameDtos.RecentGames(accounts.Get(a.Id), accounts.ResultsFor(a.Id))["gameStatistics"]).Cast<AsObject>();
            TestAssert.True(recent.All(row => Equals(row["ranked"], true)));

            // After the client's ten placement games the K falls to 32: plus 16 between equals.
            var placed = Enumerable.Range(0, 10).Select(index => new PlatformResult(100 + index, Guid.NewGuid(), [a.Id, c.Id],
                default, QueueId: PlatformGames.RankedTreelineTeamQueueId,
                Ratings: new() { [a.Id] = new(1300, 1300), [c.Id] = new(1300, 1300) })).ToArray();
            var even = new MatchResult(8, "Version 1.0.0.126", 4, "CLASSIC", "Order", 1800, [Player(a), Player(c) with { Team = "Chaos" }], false);
            var change = PlatformRatings.Rate(even, new Dictionary<ulong, long> { [(ulong)a.SummonerId] = a.Id, [(ulong)c.SummonerId] = c.Id },
                placed, "RANKED_PREMADE_3x3");
            TestAssert.Equal(new PlatformRating(1300, 1316), change[a.Id]);
        }
        finally { DeleteDirectory(root); }
    }

    private static void Ladder()
    {
        var root = TemporaryDirectory("league-air-ranked-ladder");
        try
        {
            using var accounts = new PlatformAccounts(root);
            var (a, b, c, d) = (Register(accounts, "laddera"), Register(accounts, "ladderb"), Register(accounts, "ladderc"), Register(accounts, "ladderd"));
            for (var game = 1; game <= 10; game++) Ranked(accounts, 4, game, [a], [b]);
            Ranked(accounts, 6, 11, [c], [d]);
            var html = PlatformHome.RenderLadder(accounts);
            var rating = accounts.Rating(a.Id, "RANKED_SOLO_5x5");
            TestAssert.True(html.Contains("<h2>Ranked 5x5 Solo</h2>", StringComparison.Ordinal));
            TestAssert.True(html.Contains($"<td>1</td><td>laddera</td><td>{rating}</td><td>10</td><td>0</td>", StringComparison.Ordinal),
                "ten games show the rating");
            TestAssert.True(html.Contains("<td>1</td><td>ladderc</td><td>placement 1/10</td><td>1</td><td>0</td>", StringComparison.Ordinal),
                "a rating stays hidden until ten games, as the stock profile hides it");
            TestAssert.True(html.Contains("<h2>Ranked 3x3 Team</h2>", StringComparison.Ordinal));
        }
        finally { DeleteDirectory(root); }
    }

    private static PlatformAccount Register(PlatformAccounts accounts, string name) => accounts.RegisterPublic(name, "testing-password", name);

    /// <summary>Records a finished ranked game the way the platform's Tick does.</summary>
    private static void Ranked(PlatformAccounts accounts, int queue, int gameId, PlatformAccount[] order, PlatformAccount[] chaos,
        string winner = "Order", PlatformAccount? leaver = null)
    {
        var players = order.Select(account => Player(account)).Concat(chaos.Select(account => Player(account) with { Team = "Chaos" }))
            .Select(player => leaver is not null && player.PlayerId == (ulong)leaver.SummonerId ? player with { Leaver = true } : player).ToArray();
        var map = PlatformGames.QueueOf(queue)!.MapId;
        var match = new MatchResult(8, "Version 1.0.0.126", map, GameMaps.ModeOf(map), winner, 1800, players, false);
        TestAssert.True(accounts.RecordResult(new(gameId, Guid.NewGuid(), order.Concat(chaos).Select(account => account.Id).ToArray(),
            JsonSerializer.SerializeToElement(match, MatchRoster.JsonOptions), DateTimeOffset.UtcNow, queue)));
    }
}
