using Amberline.Core;
using Amberline.Data;
using Amberline.Host;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Tests.Air;

public static class AirMatchmakingChecks
{
    [Check("server.air-normal-queue-fifo-reply-gate-and-champ-select")]
    public static void NormalQueueMatchesTenReadySoloPlayersInFifoOrder() => RunAsync().GetAwaiter().GetResult();

    [Check("server.air-normal-queue-membership-and-session-purge")]
    public static void NormalQueueRejectsPremadesAndPurgesReplacedConnections() => MembershipAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-matchmaking-" + Guid.NewGuid().ToString("N"));
        using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
        await using var manager = new GameManager(new HostSettings(GameData.Default.Paths.Root, ServerExecutable(), BindAddress: "127.0.0.1"),
            Path.Combine(root, "games"));
        var clock = new QueueClock();
        var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
        var current = new Dictionary<string, long>();
        games.AccountForConnection = connection => current.GetValueOrDefault(connection);
        var pushed = new List<(long AccountId, AsObject Body)>();
        games.Push = (accountId, body) => pushed.Add((accountId, (AsObject)body));
        var players = Enumerable.Range(0, 10).Select(index =>
            accounts.RegisterPublic("queue" + index, "testing-password", "Queue " + index)).ToArray();
        var connections = players.Select((_, index) => "conn-" + index).ToArray();
        for (var index = 0; index < players.Length; index++)
        {
            current[connections[index]] = players[index].Id;
            var notification = (AsObject)games.AttachQueue(players[index], connections[index], [2]);
            var joined = (ArrayCollection)notification["joinedQueues"];
            TestAssert.Equal(1, joined.Count, "queue attach returns one joined queue");
            TestAssert.Equal(2, ((AsObject)joined[0])["queueId"]);
            TestAssert.True(notification["playerJoinFailures"] is null, "successful queue attach has no failure collection");
            if (index == 0)
            {
                _ = games.AttachQueue(players[index], connections[index], [2]);
                TestAssert.Equal(1, ((AsObject)games.QueueInfo())["queueLength"], "reattach is idempotent");
            }
            if (index < players.Length - 1) games.ConfirmQueueReply(connections[index]);
        }

        games.Tick();
        TestAssert.True(games.Current(players[0].Id) is null, "the tenth player is not ready yet");
        games.ConfirmQueueReply(connections[^1]);
        games.Tick();

        var firstGame = (AsObject)games.Current(players[0].Id)!;
        TestAssert.Equal("CHAMP_SELECT", firstGame["gameState"]);
        TestAssert.Equal("NORMAL_GAME", firstGame["gameType"]);
        TestAssert.Equal("NORMAL", firstGame["queueTypeName"]);
        TestAssert.Equal(10, ((ArrayCollection)firstGame["teamOne"]).Count + ((ArrayCollection)firstGame["teamTwo"]).Count);
        TestAssert.Equal(5, ((ArrayCollection)firstGame["teamOne"]).Count);
        TestAssert.Equal(5, ((ArrayCollection)firstGame["teamTwo"]).Count);
        TestAssert.Equal(0, ((AsObject)games.QueueInfo())["queueLength"]);
        TestAssert.False(games.CancelQueue(players[0].Id, connections[0]),
            "a matched account cannot cancel its assigned game");
        TestAssert.True(pushed.Any(item => item.Body["gameState"] is "CHAMP_SELECT"),
            "champion selection is pushed after the queue-ready reply");
        var champSelect = firstGame;

        var orderIds = ((ArrayCollection)champSelect["teamOne"]).Cast<AsObject>()
            .Select(member => (long)(double)member["accountId"]).ToArray();
        var chaosIds = ((ArrayCollection)champSelect["teamTwo"]).Cast<AsObject>()
            .Select(member => (long)(double)member["accountId"]).ToArray();
        TestAssert.Equal(players.Take(5).Select(player => player.Id).ToArray(), orderIds, "FIFO order team one");
        TestAssert.Equal(players.Skip(5).Select(player => player.Id).ToArray(), chaosIds, "FIFO order team two");
        games.Pick(players[0].Id, 1);
        var duplicate = (AsObject)games.Pick(players[1].Id, 1);
        TestAssert.False(((ArrayCollection)duplicate["playerChampionSelections"]).Cast<AsObject>()
                .Any(selection => Equals(selection["summonerInternalName"], "sum" + players[1].SummonerId)),
            "a team cannot pick a duplicate champion: the unchanged game comes back");
        games.Quit(players[0].Id);
        TestAssert.True(games.Current(players[1].Id) is null, "normal champion-select quit terminates the matched room");
        TestAssert.Equal(9, ((AsObject)games.QueueInfo())["queueLength"],
            "the nine who stayed are back in the queue, because the client's rejoin sends no new join");
    }

    private static async Task MembershipAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-air-matchmaking-membership-" + Guid.NewGuid().ToString("N"));
        using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
        await using var manager = new GameManager(new HostSettings(GameData.Default.Paths.Root, ServerExecutable(), BindAddress: "127.0.0.1"),
            Path.Combine(root, "games"));
        var games = new PlatformGames(accounts, manager, "127.0.0.1");
        var account = accounts.RegisterPublic("queueowner", "testing-password", "Queue Owner");
        var current = new Dictionary<string, long> { ["old"] = account.Id };
        games.AccountForConnection = connection => current.GetValueOrDefault(connection);

        TestAssert.Throws<ArgumentException>(() => games.AttachQueue(account, "unknown", [2]),
            "queue attach is bound to the authenticated connection account");
        var invalid = (AsObject)games.AttachQueue(account, "old", [1]);
        var invalidPlayer = (AsObject)((ArrayCollection)invalid["playerJoinFailures"])[0];
        TestAssert.Equal("QUEUE_DISABLED", invalidPlayer["reasonFailed"]);

        // Batch 579: a team is queued only with members who accepted its invitation; this one names a summoner who never did.
        var failure = (AsObject)games.AttachQueue(account, "old", [2], team: [account.SummonerId, account.SummonerId + 1000], invitationId: "1");
        var failedPlayer = (AsObject)((ArrayCollection)failure["playerJoinFailures"])[0];
        TestAssert.Equal("QUEUE_DISABLED", failedPlayer["reasonFailed"]);
        TestAssert.Equal(0, ((AsObject)games.QueueInfo())["queueLength"]);

        _ = games.AttachQueue(account, "old", [2]);
        TestAssert.Throws<ArgumentException>(() => games.Create(account, "Custom", "", 1, 2, 1, "CLASSIC", "NONE"),
            "a queued account cannot create a custom lobby");
        current["new"] = account.Id;
        _ = games.AttachQueue(account, "new", [2]);
        current.Remove("old");
        TestAssert.Equal(1, ((AsObject)games.QueueInfo())["queueLength"], "a replacement connection preserves one queue entry");
        _ = games.CancelQueue(account.Id, "old");
        TestAssert.Equal(1, ((AsObject)games.QueueInfo())["queueLength"], "an old connection cannot cancel the replacement entry");
        current.Remove("new");
        games.Tick();
        TestAssert.Equal(0, ((AsObject)games.QueueInfo())["queueLength"]);
        TestAssert.True(games.CancelQueue(account.Id, "old"), "cancel is idempotent after session purge");

        var sessions = new PlatformSessions(accounts);
        var rpc = new PlatformServices(accounts, sessions, games);
        var session = sessions.Login("rpc", account.Username, sessions.IssueTicket(account), "1.45.11_10_04_15_28");
        TestAssert.True(sessions.AuthenticateChannel("rpc", account.Username, session.Token));
        var parameters = AirWire.Object("com.riotgames.platform.matchmaking.MatchMakerParams",
            ("queueIds", AirWire.List([2d])), ("team", null), ("invitationId", null), ("botDifficulty", null));
        var attached = (AsObject)rpc.Route("rpc", "matchmakerService", "attachToQueues", [parameters])!;
        TestAssert.True(attached["playerJoinFailures"] is null);
        TestAssert.Equal(1, ((AsObject)rpc.Route("rpc", "matchmakerService", "getQueueInfo", [2d])!)["queueLength"]);
        TestAssert.Equal(true, rpc.Route("rpc", "matchmakerService", "cancelFromQueueIfPossible", [2d]));
        _ = rpc.Route("rpc", "matchmakerService", "attachToQueues", [parameters]);
        rpc.Disconnect("rpc");
        TestAssert.Equal(0, ((AsObject)games.QueueInfo())["queueLength"], "disconnect removes queue membership");
    }

    private sealed class QueueClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string ServerExecutable() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "Amberline.Server", "release", "Amberline.Server.exe"));
}
