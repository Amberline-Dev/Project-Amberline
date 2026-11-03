using Amberline.Host;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;
using static Amberline.Tests.Air.AirQueueChecks;

namespace Amberline.Tests.Air;

/// <summary>Batch 579: party queueing, the normal draft queue 14 and the Dominion queues 16 and 17.</summary>
public static class AirPartyQueueChecks
{
    private const string SearchAlias = "com.riotgames.platform.matchmaking.SearchingForMatchNotification";
    private const string NotificationAlias = "com.riotgames.platform.game.message.GameNotification";

    [Check("server.air-queue-party-invite-accept-and-team-attach")]
    public static void PartyInviteAcceptAndTeamAttach() => InviteAsync().GetAwaiter().GetResult();

    [Check("server.air-queue-party-member-leaving-disbands-the-team")]
    public static void PartyMemberLeavingDisbandsTheTeam() => LeavingAsync().GetAwaiter().GetResult();

    [Check("server.air-queue-party-coop-plays-on-one-team")]
    public static void PartyCoopPlaysOnOneTeam() => CoopAsync().GetAwaiter().GetResult();

    [Check("server.air-queue-normal-draft-bans-then-picks")]
    public static void NormalDraftBansThenPicks() => DraftAsync().GetAwaiter().GetResult();

    [Check("server.air-queue-dominion-blind-and-draft-on-the-crystal-scar")]
    public static void DominionBlindAndDraft() => DominionAsync().GetAwaiter().GetResult();

    [Check("server.air-queue-dominion-results-count-as-odin-unranked")]
    public static void DominionResultsCountAsOdinUnranked() => DominionResults();

    private static async Task InviteAsync()
    {
        var root = TemporaryDirectory("league-air-queue-party");
        try
        {
            await using var host = new Rig(root);
            var leader = host.SignIn("partylead");
            var friend = host.SignIn("partyfriend");
            var other = host.SignIn("partyother");
            double[] team = [leader.SummonerId, friend.SummonerId, other.SummonerId];

            // Nobody accepted yet: the team is refused in the shape the stock join handler reads (QUEUE_DISABLED).
            TestAssert.Equal("QUEUE_DISABLED", Failure(host.Attach(leader, "attachTeamToQueue", 2, team, "4242")));
            // The invitee's accept answers null; its success handler only sends the accept chat message to the inviter.
            TestAssert.True(host.Rpc.Route(friend.Username, "matchmakerService", "acceptInviteForMatchmakingGame", ["4242"]) is null);
            TestAssert.True(host.Rpc.Route(other.Username, "matchmakerService", "acceptInviteForMatchmakingGame", ["777"]) is null);
            TestAssert.Equal("QUEUE_DISABLED", Failure(host.Attach(leader, "attachTeamToQueue", 2, team, "4242")),
                "a member who accepted another invitation is not on this team");
            TestAssert.Equal(0, ((AsObject)host.Games.QueueInfo())["queueLength"]);

            // Four solo players arrive first, then the party of three, then three more solo players.
            var solos = Enumerable.Range(0, 7).Select(index => host.SignIn("partysolo" + index)).ToArray();
            foreach (var solo in solos.Take(4)) host.Queue(solo, 2);
            _ = host.Rpc.Route(other.Username, "matchmakerService", "acceptInviteForMatchmakingGame", ["4242"]);
            var joined = host.Attach(leader, "attachTeamToQueue", 2, team, "4242");
            TestAssert.True(joined["playerJoinFailures"] is null);
            TestAssert.Equal(2, ((AsObject)((ArrayCollection)joined["joinedQueues"])[0])["queueId"]);
            TestAssert.Equal(7, ((AsObject)host.Games.QueueInfo())["queueLength"], "the party's three entries joined");
            // The members wait in their arranged-team view and enter the queue on a pushed SearchingForMatchNotification.
            foreach (var member in new[] { friend, other })
                TestAssert.True(host.Pushed.Any(push => push.Account == member.Id && push.Body.TypeName == SearchAlias &&
                    ((ArrayCollection)push.Body["joinedQueues"]).Count == 1), "each member is told the team joined");
            TestAssert.False(host.Pushed.Any(push => push.Account == leader.Id), "the leader has the call's own answer");
            foreach (var solo in solos.Skip(4)) host.Queue(solo, 2);

            // The leader's reply gates the whole party.
            host.Games.Tick();
            TestAssert.True(host.Games.Current(leader.Id) is null, "the party is not matched before its leader's reply was sent");
            host.Rpc.ResponseSent(leader.Username, "matchmakerService", "attachTeamToQueue");
            host.Games.Tick();
            var game = (AsObject)host.Games.Current(leader.Id)!;
            TestAssert.Equal("CHAMP_SELECT", game["gameState"]);
            var one = Accounts(game, "teamOne");
            var two = Accounts(game, "teamTwo");
            TestAssert.Equal(5, one.Length);
            TestAssert.Equal(5, two.Length);
            // First fit in arrival order: the four solo players take team one, the party does not fit beside them and plays team two.
            TestAssert.Equal(new[] { leader.Id, friend.Id, other.Id }, two.Take(3).ToArray(), "the party plays together, on one team");
            TestAssert.Equal(solos.Take(4).Select(solo => solo.Id).ToArray(), one.Take(4).ToArray());

            // An invitee who is already in a game is refused in the one class the accept's call site handles.
            string? alias = null;
            try { host.Rpc.Route(friend.Username, "matchmakerService", "acceptInviteForMatchmakingGame", ["5000"]); }
            catch (AirFault fault) { alias = fault.Alias; }
            TestAssert.Equal("com.riotgames.platform.matchmaking.MatchmakingException", alias);
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task LeavingAsync()
    {
        var root = TemporaryDirectory("league-air-queue-party-leave");
        try
        {
            await using var host = new Rig(root);
            var leader = host.SignIn("leavelead");
            var friend = host.SignIn("leavefriend");
            host.Party(leader, [friend], 2, "11");
            TestAssert.Equal(2, ((AsObject)host.Games.QueueInfo())["queueLength"]);

            // A member cancels: the whole team leaves the queue and the others are told who left, the stock "{0} left the game.
            // Your team has been disbanded." (the client prefixes "sum" to the argument to look the name up).
            TestAssert.Equal(true, host.Rpc.Route(friend.Username, "matchmakerService", "cancelFromQueueIfPossible", [2d]));
            TestAssert.Equal(0, ((AsObject)host.Games.QueueInfo())["queueLength"], "the team is disbanded");
            var quit = host.Pushed.Single(push => push.Body.TypeName == NotificationAlias);
            TestAssert.Equal(leader.Id, quit.Account);
            TestAssert.Equal("PLAYER_QUIT", quit.Body["type"]);
            TestAssert.Equal(friend.SummonerId.ToString(System.Globalization.CultureInfo.InvariantCulture), quit.Body["messageArgument"]);

            // A lost connection disbands the team the same way.
            host.Party(leader, [friend], 2, "12");
            host.Rpc.ConnectionClosed(leader.Username);
            TestAssert.Equal(0, ((AsObject)host.Games.QueueInfo())["queueLength"]);
            TestAssert.Equal(friend.Id, host.Pushed.Last(push => push.Body.TypeName == NotificationAlias).Account);

            // Two parties and six solo players form a game; a member of the first party quits champion selection.
            var a = host.SignIn("leavea");
            var a1 = host.SignIn("leavea1");
            var b = host.SignIn("leaveb");
            var b1 = host.SignIn("leaveb1");
            host.Party(a, [a1], 2, "21");
            host.Party(b, [b1], 2, "22");
            var solos = Enumerable.Range(0, 6).Select(index => host.SignIn("leavesolo" + index)).ToArray();
            foreach (var solo in solos) host.Queue(solo, 2);
            host.Games.Tick();
            TestAssert.True(host.Games.Current(a.Id) is not null && host.Games.Current(b1.Id) is not null);
            host.Games.Quit(a1.Id);
            // The leaver's party mate goes home (the client sees a teammate among the leavers and does not rejoin); everyone else is
            // back in the queue, the other party still one unit.
            TestAssert.True(host.Games.Current(a.Id) is null);
            TestAssert.Equal(8, ((AsObject)host.Games.QueueInfo())["queueLength"], "the second party and the six solo players are back");
            foreach (var late in Enumerable.Range(0, 2).Select(index => host.SignIn("leavelate" + index))) host.Queue(late, 2);
            host.Games.Tick();
            var again = (AsObject)host.Games.Current(b.Id)!;
            var sameTeam = Accounts(again, "teamOne").Contains(b.Id) ? Accounts(again, "teamOne") : Accounts(again, "teamTwo");
            TestAssert.True(sameTeam.Contains(b1.Id), "the re-queued party still plays on one team");
            TestAssert.True(host.Games.Current(a.Id) is null, "the leaver's party mate was not put back");
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task CoopAsync()
    {
        var root = TemporaryDirectory("league-air-queue-party-coop");
        try
        {
            await using var host = new Rig(root);
            var beginner = host.SignIn("coopbeginner");
            _ = host.Attach(beginner, "attachToQueue", PlatformGames.CoopVsAiQueueId, null, null, "EASY");
            host.Rpc.ResponseSent(beginner.Username, "matchmakerService", "attachToQueue");
            var leader = host.SignIn("cooplead");
            var friends = new[] { host.SignIn("coopfriend1"), host.SignIn("coopfriend2") };
            host.Party(leader, friends, PlatformGames.CoopVsAiQueueId, "31", "MEDIUM");
            host.Clock.Now = host.Clock.Now.AddSeconds(5);
            host.Games.Tick();

            var game = (AsObject)host.Games.Current(leader.Id)!;
            var one = ((ArrayCollection)game["teamOne"]).Cast<AsObject>().ToArray();
            TestAssert.Equal(new object[] { (double)leader.Id, (double)friends[0].Id, (double)friends[1].Id },
                one.Take(3).Select(player => player["accountId"]).ToArray(), "the party plays together on team one");
            TestAssert.True(one.Skip(3).Concat(((ArrayCollection)game["teamTwo"]).Cast<AsObject>())
                .All(bot => bot.TypeName == BotAlias && Equals(bot["botSkillLevel"], 1d)), "the leader's Intermediate bots fill the rest");
            var alone = (AsObject)host.Games.Current(beginner.Id)!;
            TestAssert.False(Equals(alone["id"], game["id"]), "a Beginner player never joins an Intermediate party");
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task DraftAsync()
    {
        var root = TemporaryDirectory("league-air-queue-normal-draft");
        try
        {
            await using var host = new Rig(root);
            var listed = ((ArrayCollection)host.Rpc.Route(host.SignIn("draftviewer").Username, "matchmakerService", "getAvailableQueues", [])!)
                .Cast<AsObject>().Single(queue => Equals(queue["id"], 14d));
            TestAssert.Equal("NORMAL", listed["type"]);
            TestAssert.Equal(AirGameDtos.DraftConfigId, listed["gameTypeConfigId"]);
            TestAssert.Equal("Normal 5v5 Draft", listed["name"]);
            TestAssert.Equal(new object[] { 1d }, ((ArrayCollection)listed["supportedMapIds"]).Cast<object>().ToArray());

            var players = Enumerable.Range(0, 10).Select(index => host.SignIn("draftqueue" + index)).ToArray();
            foreach (var player in players) host.Queue(player, PlatformGames.NormalDraftQueueId);
            host.Games.Tick();
            var game = (AsObject)host.Games.Current(players[0].Id)!;
            TestAssert.Equal("PRE_CHAMP_SELECT", game["gameState"], "a draft queue game opens with the bans");
            TestAssert.Equal(AirGameDtos.DraftConfigId, game["gameTypeConfigId"]);
            TestAssert.Equal("NORMAL_GAME", game["gameType"]);
            TestAssert.Equal(1, game["pickTurn"]);
            TestAssert.Equal(players.Take(5).Select(player => player.Id).ToArray(), Accounts(game, "teamOne"));

            // Order's captain bans first; then Chaos's. A ban out of turn is the client's PG-0030.
            TestAssert.Throws<AirFault>(() => host.Games.Ban(players[5].Id, 1));
            host.Games.Ban(players[0].Id, 1);
            host.Games.Ban(players[5].Id, 22);
            for (var turn = 3; turn <= PlatformGames.DraftMaxBans; turn++)
            {
                host.Clock.Now = host.Clock.Now.AddSeconds(PlatformGames.DraftBanServerSeconds);
                host.Games.Tick();
            }
            game = (AsObject)host.Games.Current(players[0].Id)!;
            TestAssert.Equal("CHAMP_SELECT", game["gameState"]);
            TestAssert.Equal(new object[] { 1, 22 }, ((ArrayCollection)game["bannedChampionIds"]).Cast<object>().ToArray());
            // Pick turns 1-2-2-2-2-1: Order's first player alone, then two of Chaos.
            TestAssert.Throws<AirFault>(() => host.Games.Pick(players[5].Id, 61), "not Chaos's turn yet");
            host.Games.Pick(players[0].Id, 61);
            host.Games.LockIn(players[0].Id);
            TestAssert.Equal(2, ((AsObject)host.Games.Current(players[0].Id)!)["pickTurn"]);
            host.Games.Pick(players[5].Id, 64);
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task DominionAsync()
    {
        var root = TemporaryDirectory("league-air-queue-dominion");
        try
        {
            await using var host = new Rig(root);
            var queues = ((ArrayCollection)host.Rpc.Route(host.SignIn("odinviewer").Username, "matchmakerService", "getAvailableQueues", [])!)
                .Cast<AsObject>().ToDictionary(queue => (double)queue["id"]);
            foreach (var (id, config) in new[] { (16d, AirGameDtos.BlindPickConfigId), (17d, AirGameDtos.DraftConfigId) })
            {
                TestAssert.Equal("ODIN_UNRANKED", queues[id]["type"], "the client's QueueType.DOMINION_UNRANKED");
                TestAssert.Equal("ODIN", queues[id]["gameMode"]);
                TestAssert.Equal(5d, queues[id]["numPlayersPerTeam"]);
                TestAssert.Equal(config, queues[id]["gameTypeConfigId"]);
                TestAssert.Equal(new object[] { 8d }, ((ArrayCollection)queues[id]["supportedMapIds"]).Cast<object>().ToArray());
            }

            var blind = Enumerable.Range(0, 10).Select(index => host.SignIn("odinblind" + index)).ToArray();
            foreach (var player in blind) host.Queue(player, PlatformGames.DominionQueueId);
            var draft = Enumerable.Range(0, 10).Select(index => host.SignIn("odindraft" + index)).ToArray();
            foreach (var player in draft) host.Queue(player, PlatformGames.DominionDraftQueueId);
            host.Games.Tick();
            var game = (AsObject)host.Games.Current(blind[0].Id)!;
            TestAssert.Equal("CHAMP_SELECT", game["gameState"]);
            TestAssert.Equal(8, game["mapId"]);
            TestAssert.Equal("ODIN", game["gameMode"]);
            TestAssert.Equal("ODIN_UNRANKED", game["queueTypeName"]);
            TestAssert.Equal(10, game["maxNumPlayers"]);
            TestAssert.Equal($"Dominion Game {(int)(double)game["id"]}", game["name"]);
            var drafted = (AsObject)host.Games.Current(draft[0].Id)!;
            TestAssert.Equal("PRE_CHAMP_SELECT", drafted["gameState"]);
            TestAssert.Equal(8, drafted["mapId"]);

            // Blind pick, then a ten-seat launch with the spells The Crystal Scar offers.
            var champions = AirGameDtos.ChampionNames.Keys.Take(5).ToArray();
            for (var index = 0; index < blind.Length; index++)
            {
                host.Games.Pick(blind[index].Id, champions[index % 5]);
                host.Games.LockIn(blind[index].Id);
            }
            host.Clock.Now = host.Clock.Now.AddSeconds(PlatformGames.PostPickServerSeconds);
            host.Games.Tick();
            TestAssert.Equal("START_REQUESTED", ((AsObject)host.Games.Current(blind[0].Id)!)["gameState"]);
            var roster = MatchRoster.Read(Path.Combine(Directory.EnumerateDirectories(Path.Combine(root, "games")).Single(), "roster.json"));
            TestAssert.Equal(10, roster.Length);
            var offered = AirGameDtos.SpellNamesFor(8).Values.ToHashSet();
            TestAssert.True(roster.All(seat => offered.Contains(seat.SummonerSpell1) && offered.Contains(seat.SummonerSpell2)), "map 8's spells");
        }
        finally { DeleteDirectory(root); }
    }

    private static void DominionResults()
    {
        var root = TemporaryDirectory("league-air-queue-dominion-results");
        try
        {
            File.WriteAllText(Path.Combine(root, "economy.json"), "{\"PracticeMinutesPerDay\":10}");
            using var accounts = new PlatformAccounts(root);
            var account = accounts.RegisterPublic("odinresult", "testing-password", "Odin Result", maxed: false);
            var noon = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
            var match = new MatchResult(8, "Version 1.0.0.126", 8, "ODIN", "Order", 360, [Player(account)], false);
            TestAssert.True(accounts.RecordResult(new(1, Guid.NewGuid(), [account.Id],
                System.Text.Json.JsonSerializer.SerializeToElement(match, MatchRoster.JsonOptions), noon, PlatformGames.DominionQueueId)));
            // A normal queue game: off the practice budget, and it claims First Win of the Day.
            TestAssert.Equal(0d, PlatformEconomy.Practice(accounts.Get(account.Id), accounts.EconomyRules, noon).SecondsSpent);
            TestAssert.True(PlatformEconomy.For(accounts.Get(account.Id)).Ip > PlatformEconomy.FirstWinBonusIp);
            // The profile reads Dominion wins from OdinUnranked (QueueType ODIN_UNRANKED), never from the classic Unranked record.
            var summaries = ((ArrayCollection)AirProfileDtos.PlayerStatSummaries(accounts.Get(account.Id), accounts.ResultsFor(account.Id))
                ["playerStatSummarySet"]).Cast<AsObject>().ToDictionary(row => (string)row["playerStatSummaryType"]);
            TestAssert.Equal(1, summaries["OdinUnranked"]["wins"]);
            TestAssert.Equal(0, summaries["Unranked"]["wins"]);
            TestAssert.Equal((0, 0, 0), AirProfileDtos.Record(accounts.Get(account.Id), accounts.ResultsFor(account.Id)));
        }
        finally { DeleteDirectory(root); }
    }

    internal static string Failure(AsObject notification) =>
        (string)((AsObject)((ArrayCollection)notification["playerJoinFailures"])[0])["reasonFailed"];

    internal static long[] Accounts(AsObject game, string team) =>
        ((ArrayCollection)game[team]).Cast<AsObject>().Select(player => (long)(double)player["accountId"]).ToArray();

    /// <summary>A platform with signed-in RTMP sessions: each account's connection is its user name (also the ranked checks' rig).</summary>
    internal sealed class Rig : IAsyncDisposable
    {
        public readonly PlatformAccounts Accounts;
        public readonly GameManager Manager;
        public readonly ManualClock Clock = new();
        public readonly List<(long Account, AsObject Body)> Pushed = [];
        public readonly PlatformGames Games;
        public readonly PlatformSessions Sessions;
        public readonly PlatformServices Rpc;

        public Rig(string root)
        {
            Accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            Manager = NonLaunchingManager(root);
            Games = new PlatformGames(Accounts, Manager, "127.0.0.1", Clock);
            Games.Push = (account, body) => Pushed.Add((account, (AsObject)body));
            Sessions = new PlatformSessions(Accounts);
            Rpc = new PlatformServices(Accounts, Sessions, Games);
        }

        public PlatformAccount SignIn(string name, bool maxed = true)
        {
            var account = Accounts.RegisterPublic(name, "testing-password", name, maxed);
            var session = Sessions.Login(account.Username, account.Username, Sessions.IssueTicket(account), AirQueueChecks.Version);
            TestAssert.True(Sessions.AuthenticateChannel(account.Username, account.Username, session.Token));
            return account;
        }

        public AsObject Attach(PlatformAccount who, string operation, int queue, double[]? team, string? invitation, string? difficulty = null) =>
            (AsObject)Rpc.Route(who.Username, "matchmakerService", operation,
                [AirWire.Object("com.riotgames.platform.matchmaking.MatchMakerParams", ("queueIds", AirWire.List([(double)queue])),
                    ("team", team is null ? null : AirWire.List(team.Cast<object>())), ("invitationId", invitation), ("botDifficulty", difficulty))])!;

        /// <summary>A solo player joins and the reply is sent, as the stock attachToQueue does.</summary>
        public void Queue(PlatformAccount who, int queue)
        {
            TestAssert.True(Attach(who, "attachToQueue", queue, null, null)["playerJoinFailures"] is null);
            Rpc.ResponseSent(who.Username, "matchmakerService", "attachToQueue");
        }

        /// <summary>The stock party flow: each member accepts the invitation, then the leader attaches the team and its reply is sent.</summary>
        public void Party(PlatformAccount leader, PlatformAccount[] members, int queue, string invitation, string? difficulty = null)
        {
            foreach (var member in members)
                _ = Rpc.Route(member.Username, "matchmakerService", "acceptInviteForMatchmakingGame", [invitation]);
            var team = members.Prepend(leader).Select(member => (double)member.SummonerId).ToArray();
            TestAssert.True(Attach(leader, "attachTeamToQueue", queue, team, invitation, difficulty)["playerJoinFailures"] is null);
            Rpc.ResponseSent(leader.Username, "matchmakerService", "attachTeamToQueue");
        }

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            Accounts.Dispose();
        }
    }
}
