using Amberline.Core;
using Amberline.Host;
using Amberline.Platform;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Tests.Air;

/// <summary>
/// The lobby answers the stock client reads as values or typed platform exceptions: switchTeams is a Boolean, a taken
/// champion returns the unchanged game, refusals carry the PG-xxxx codes of the client's resource bundle, and a player
/// who leaves champion selection takes the others back to team selection (or back to the queue).
/// </summary>
public static class AirLobbyFaultChecks
{
    [Check("server.air-lobby-typed-refusals-and-values")]
    public static void LobbyRefusalsCarryRetailCodesAndTwoAnswersAreValues() => RefusalsAsync().GetAwaiter().GetResult();

    [Check("server.air-locked-champions-are-listed-and-cannot-be-picked")]
    public static void UnownedChampionsAreListedLockedAndCannotBePicked() => LockedAsync().GetAwaiter().GetResult();

    [Check("server.air-champion-select-leaver-returns-the-others")]
    public static void ALeaverFromChampionSelectReturnsTheOthersToTeamSelect() => LeaverAsync().GetAwaiter().GetResult();

    [Check("server.air-normal-queue-leaver-requeues-the-rest")]
    public static void AMatchedPlayerWhoLeavesChampionSelectPutsTheOthersBackInTheQueue() => RequeueAsync().GetAwaiter().GetResult();

    private static AirFault Fault(Action action)
    {
        try { action(); }
        catch (AirFault fault) { return fault; }
        throw new InvalidOperationException("The call was expected to be refused with a typed platform exception.");
    }

    private static int Id(object lobby) => Convert.ToInt32(((AsObject)lobby)["id"]);
    private static long Revision(PlatformGames games, long account, int id) =>
        (long)(double)((AsObject)games.Get(account, id))["optimisticLock"];

    private static async Task RefusalsAsync()
    {
        var root = Directory.CreateTempSubdirectory("league-air-lobby-faults").FullName;
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = NonLaunchingManager(root);
            var games = new PlatformGames(accounts, manager, "127.0.0.1");
            games.Push = (_, _) => { };
            PlatformAccount NewAccount(string name) => accounts.RegisterPublic(name.ToLowerInvariant(), "testing-password", name);
            var a = NewAccount("FaultA");
            var b = NewAccount("FaultB");
            var c = NewAccount("FaultC");
            var d = NewAccount("FaultD");

            var id = Id(games.Create(a, "Typed", "pw", 1, 4, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE"));
            TestAssert.Equal("PG-0013", Fault(() => games.Create(b, "typed", "", 1, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE")).ErrorCode,
                "an open custom game already has that name");
            TestAssert.Equal("PG-0014", Fault(() => games.Join(b, id + 100, "")).ErrorCode);
            var wrong = Fault(() => games.Join(b, id, "nope"));
            TestAssert.Equal("PG-0010", wrong.ErrorCode);
            TestAssert.Equal("com.riotgames.platform.game.IncorrectPasswordException", wrong.Alias);
            games.Join(b, id, "pw");
            games.Join(c, id, "pw");
            games.Join(d, id, "pw");
            var e = NewAccount("FaultE");
            var other = Id(games.Create(e, "Other", "", 1, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE"));
            TestAssert.Equal("PG-0008", Fault(() => games.Join(b, other, "")).ErrorCode, "joining another game while in one");
            TestAssert.Equal("PG-0012", Fault(() => games.Create(b, "Elsewhere", "", 1, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE")).ErrorCode);

            // Teams of two: the owner and C are Order, B and D are Chaos.
            TestAssert.False(games.SwitchTeam(c.Id, id), "the other team is full: the Boolean answer is false, not a fault");
            TestAssert.Equal("PG-0002", Fault(() => games.Start(a.Id, id, Revision(games, a.Id, id) - 1)).ErrorCode,
                "a stale start revision");
            games.Start(a.Id, id, Revision(games, a.Id, id));
            TestAssert.Equal("PG-0018", Fault(() => games.SwitchTeam(c.Id, id)).ErrorCode, "teams are locked once selection started");

            // A teammate already holds Annie: the answer is the unchanged game, not a fault.
            games.Pick(a.Id, 1);
            var answer = (AsObject)games.Pick(c.Id, 1);
            var selections = ((ArrayCollection)answer["playerChampionSelections"]).Cast<AsObject>().ToArray();
            TestAssert.Equal(1, selections.Length, "only the first pick is in the game");
            TestAssert.Equal(1, selections[0]["championId"]);
            games.Pick(b.Id, 1);

            games.LockIn(a.Id);
            TestAssert.Equal("PG-0015", Fault(() => games.Pick(a.Id, 61)).ErrorCode, "a locked-in player cannot change the pick");
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task LockedAsync()
    {
        var root = Directory.CreateTempSubdirectory("league-air-locked-champions").FullName;
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = NonLaunchingManager(root);
            var games = new PlatformGames(accounts, manager, "127.0.0.1");
            games.Push = (_, _) => { };
            // An unlocked account owns everything the server plays: all 84 are listed owned (none is locked in the grid), their skins too,
            // and each one is pickable (every champion plays since 26 October 2026, batch 341).
            var unlocked = accounts.RegisterPublic("lockedowner", "testing-password", "Locked Owner");
            var champions = AirDtos.Champions(unlocked, accounts.EconomyRules).Cast<AsObject>().ToDictionary(c => (int)c["championId"]);
            TestAssert.Equal(84, champions.Count, "all 84 champions of the patch are listed");
            TestAssert.True(champions.Values.All(champion => (bool)champion["owned"]), "none is locked in the grid");
            TestAssert.True(((ArrayCollection)champions[2]["championSkins"]).Cast<AsObject>().All(skin => (bool)skin["owned"]),
                "Olaf's skins are owned too");

            var id = Convert.ToInt32(((AsObject)games.Create(unlocked, "Locked", "", 1, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE"))["id"]);
            games.Start(unlocked.Id, id, (long)(double)((AsObject)games.Get(unlocked.Id, id))["optimisticLock"]);
            foreach (var championId in champions.Keys) games.Pick(unlocked.Id, championId);
            TestAssert.Throws<ArgumentException>(() => games.Pick(unlocked.Id, 9999), "an unknown champion id is refused");

            // The one champion the grid still shows locked is an unowned one: a fresh account owns none and Olaf is not in the free rotation,
            // so his pick is refused as not owned (PG-0022). That refusal is the server's real one, kept for an unowned champion.
            var fresh = accounts.RegisterPublic("lockedfresh", "testing-password", "Locked Fresh", maxed: false);
            var freshOlaf = AirDtos.Champions(fresh, accounts.EconomyRules).Cast<AsObject>().ToDictionary(c => (int)c["championId"])[2];
            TestAssert.False((bool)freshOlaf["owned"], "an unowned champion is listed locked");
            TestAssert.False((bool)freshOlaf["freeToPlay"], "and is not in the free rotation");
            TestAssert.True(((ArrayCollection)freshOlaf["championSkins"]).Cast<AsObject>().All(skin => !(bool)skin["owned"]),
                "and its skins are unowned too");
            var freshId = Convert.ToInt32(((AsObject)games.Create(fresh, "Unowned", "", 1, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE"))["id"]);
            games.Start(fresh.Id, freshId, (long)(double)((AsObject)games.Get(fresh.Id, freshId))["optimisticLock"]);
            TestAssert.Equal("PG-0022", Fault(() => games.Pick(fresh.Id, 2)).ErrorCode, "an unowned champion cannot be picked");
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task LeaverAsync()
    {
        var root = Directory.CreateTempSubdirectory("league-air-lobby-leaver").FullName;
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = NonLaunchingManager(root);
            var clock = new ManualClock();
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
            var pushed = new List<(long Account, AsObject Body)>();
            games.Push = (account, body) => pushed.Add((account, (AsObject)body));
            PlatformAccount NewAccount(string name) => accounts.RegisterPublic(name.ToLowerInvariant(), "testing-password", name);
            var a = NewAccount("LeaverA");
            var b = NewAccount("LeaverB");
            var c = NewAccount("LeaverC");

            var id = Id(games.Create(a, "Leavers", "", 1, 6, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE"));
            games.Join(b, id, "");
            games.Join(c, id, "");
            games.Start(a.Id, id, Revision(games, a.Id, id));
            games.Pick(a.Id, 1);
            games.Pick(b.Id, 61);
            pushed.Clear();

            // B quits during champion selection: A and C return to team selection with their picks cleared.
            games.Quit(b.Id);
            var back = (AsObject)games.Current(a.Id)!;
            TestAssert.Equal("TEAM_SELECT", back["gameState"]);
            TestAssert.Equal(0, ((ArrayCollection)back["playerChampionSelections"]).Count, "the picks start over");
            TestAssert.Equal(2, ((ArrayCollection)back["teamOne"]).Count + ((ArrayCollection)back["teamTwo"]).Count);
            TestAssert.True(pushed.Any(p => p.Account == a.Id && p.Body["gameState"] is "TEAM_SELECT") &&
                            pushed.Any(p => p.Account == c.Id), "the others receive the team-select game");
            TestAssert.True(pushed.All(p => p.Account != b.Id), "the quitter asked for it and is not pushed to");
            TestAssert.True(games.Current(b.Id) is null);

            // C makes no pick before the deadline: C is dropped like a quitter, with a TERMINATED push.
            games.Start(a.Id, id, Revision(games, a.Id, id));
            games.Pick(a.Id, 1);
            pushed.Clear();
            clock.Now = clock.Now.AddSeconds(PlatformGames.MainPickServerSeconds + 1);
            games.Tick();
            TestAssert.True(games.Current(c.Id) is null, "the idle player is no longer in the game");
            TestAssert.Equal("TERMINATED", pushed.Last(p => p.Account == c.Id).Body["gameState"]);
            var remaining = (AsObject)games.Current(a.Id)!;
            TestAssert.Equal("TEAM_SELECT", remaining["gameState"]);
            TestAssert.Equal((double)a.Id, ((AsObject)remaining["ownerSummary"])["accountId"]);
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task RequeueAsync()
    {
        var root = Directory.CreateTempSubdirectory("league-air-queue-leaver").FullName;
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = NonLaunchingManager(root);
            var games = new PlatformGames(accounts, manager, "127.0.0.1");
            var connections = new Dictionary<string, long>();
            games.AccountForConnection = connection => connections.GetValueOrDefault(connection);
            var pushed = new List<(long Account, AsObject Body)>();
            games.Push = (account, body) => pushed.Add((account, (AsObject)body));
            var players = Enumerable.Range(0, 10).Select(index =>
                accounts.RegisterPublic("requeue" + index, "testing-password", "Requeue " + index)).ToArray();
            for (var index = 0; index < players.Length; index++)
            {
                connections["conn-" + index] = players[index].Id;
                games.AttachQueue(players[index], "conn-" + index, [2]);
                games.ConfirmQueueReply("conn-" + index);
            }
            games.Tick();
            TestAssert.Equal("CHAMP_SELECT", ((AsObject)games.Current(players[0].Id)!)["gameState"]);

            // A late joiner queues behind the nine who are about to come back.
            var late = accounts.RegisterPublic("requeuelate", "testing-password", "Requeue Late");
            connections["conn-late"] = late.Id;
            games.AttachQueue(late, "conn-late", [2]);
            games.ConfirmQueueReply("conn-late");
            pushed.Clear();
            games.Quit(players[0].Id);
            TestAssert.Equal(10, ((AsObject)games.QueueInfo())["queueLength"], "nine returning players and the late joiner");
            TestAssert.True(pushed.Count(p => p.Body["gameState"] is "TERMINATED") == 9, "the nine others are told the game ended");
            var terminated = pushed.First(p => p.Body["gameState"] is "TERMINATED").Body;
            TestAssert.Equal(9, ((ArrayCollection)terminated["teamOne"]).Count + ((ArrayCollection)terminated["teamTwo"]).Count,
                "the game no longer lists the leaver, which is how the client finds who left");
            games.Tick();
            var next = (AsObject)games.Current(players[1].Id)!;
            TestAssert.Equal("CHAMP_SELECT", next["gameState"], "ten ready players match again");
            TestAssert.True(games.Current(late.Id) is not null, "the late joiner is the tenth player");
            TestAssert.True(games.Current(players[0].Id) is null);
        }
        finally { DeleteDirectory(root); }
    }

    // The placeholder executable never starts: only the lobby matters here.
    private static GameManager NonLaunchingManager(string root)
    {
        var executable = Path.Combine(root, "server-placeholder.exe");
        Directory.CreateDirectory(Path.Combine(root, "game-data"));
        File.WriteAllBytes(executable, []);
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        return new GameManager(new HostSettings(Path.Combine(root, "game-data"), executable,
            FirstGamePort: port, MaxGames: 1, BindAddress: "127.0.0.1"), Path.Combine(root, "games"));
    }

    private static void DeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
