using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Amberline.Core;
using Amberline.Host;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using Microsoft.AspNetCore.Http;

namespace Amberline.Tests.Air;

public static class PlatformChecks
{
    private const string Version = "1.45.11_10_04_15_28";

    [Check("server.air-login-failure-retry-contract")]
    public static void CredentialFailuresUseTheStockAlertPathAndAllowRetry()
    {
        var root = TemporaryDirectory("league-air-login-retry");
        try
        {
            using var accounts = new PlatformAccounts(root);
            var account = Register(accounts, "retry", "Retry");
            var sessions = new PlatformSessions(accounts);
            JsonElement Send(string payload, int expectedStatus)
            {
                var body = Encoding.UTF8.GetBytes("payload=" + Uri.EscapeDataString(payload));
                using var input = new MemoryStream(body);
                var context = new DefaultHttpContext();
                context.Request.ContentType = "application/x-www-form-urlencoded";
                context.Request.ContentLength = body.Length;
                context.Request.Body = input;
                var result = Amberline.Platform.Program.AuthenticateAsync(context, accounts, sessions).GetAwaiter().GetResult();
                TestAssert.Equal(expectedStatus, ((IStatusCodeHttpResult)result).StatusCode ?? 200);
                return JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value);
            }

            foreach (var payload in new[] { "user=retry,password=wrong-password", "user=missing,password=secret-password", "malformed" })
            {
                // LoginView.onAuthFailed only shows the credentials alert on 403;
                // onAlertDismissed then clears loggingIn and restores both inputs.
                var failed = Send(payload, 403);
                TestAssert.Equal("invalid_credentials", failed.GetProperty("reason").GetString());
                TestAssert.False(failed.TryGetProperty("token", out _));
            }
            var valid = Send("user=retry,password=secret-password", 200);
            TestAssert.Equal("LOGIN", valid.GetProperty("status").GetString());
            var ticket = valid.GetProperty("token").GetString()!;
            var session = sessions.Login("same-client", account.Username, ticket, Version);
            TestAssert.True(sessions.AuthenticateChannel("same-client", account.Username, session.Token));
            TestAssert.Equal(account.Id, sessions.Require("same-client").AccountId);
        }
        finally { DeleteDirectory(root); }
    }

    // Invitations are retired (25 October 2026), but a store written before that still loads: its retired field is only ignored.
    [Check("server.air-platform-store-keeps-retired-invitations")]
    public static void StoreWithRetiredInvitationsStillLoads()
    {
        var root = TemporaryDirectory("league-air-legacy-store");
        try
        {
            using (var accounts = new PlatformAccounts(root))
                accounts.RegisterPublic("legacy", "secret-password", "Legacy");
            var path = Path.Combine(root, "accounts.json");
            var store = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            store["Invitations"] = System.Text.Json.Nodes.JsonNode.Parse("[\"0123456789abcdef\"]");
            File.WriteAllText(path, store.ToJsonString());
            using var reopened = new PlatformAccounts(root);
            TestAssert.True(reopened.Authenticate("legacy", "secret-password") is not null,
                "a store with retired invitations still loads and keeps its accounts");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("server.air-platform-accounts")]
    public static void AccountsRegisterPersistAndDedupeReceipts()
    {
        var root = TemporaryDirectory("league-air-accounts");
        PlatformAccount alice;
        try
        {
            using (var accounts = new PlatformAccounts(root))
            {
                TestAssert.Throws<ArgumentException>(() => accounts.RegisterPublic("alice", "short", "Alice"));
                alice = accounts.RegisterPublic("Alice", "secret-password", "Alice");
                TestAssert.Throws<ArgumentException>(() => accounts.RegisterPublic("ALICE", "secret-password", "Alice Two"),
                    "usernames are case-insensitive");
                TestAssert.Throws<ArgumentException>(() => accounts.RegisterPublic("alice_two", "secret-password", "alice"),
                    "summoner names are case-insensitive");
                var bob = accounts.RegisterPublic("Bob_2", "another-password", "Bob Two");
                TestAssert.True(accounts.Authenticate("bob_2", "another-password") is not null);
                TestAssert.True(accounts.Authenticate("bob_2", "wrong-password") is null);
                TestAssert.Equal(alice.Id + 1, bob.Id);
            }

            using (var reopened = new PlatformAccounts(root))
            {
                TestAssert.True(reopened.Authenticate("ALICE", "secret-password") is not null,
                    "registered credentials must survive reopening the store");
                TestAssert.True(reopened.Authenticate("alice", "wrong-password") is null);

                var result = new MatchResult(8, "Version 1.0.0.126", 1, "CLASSIC", "Order", 42,
                    [new PlayerMatchResult((ulong)alice.SummonerId, 1, alice.Summoner, "Annie", "Order", 0,
                        true, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 500, 0,
                        new HeroMatchStatistics().Snapshot(42), ["SummonerFlash", "SummonerDot"], [0, 0, 0, 0], [])], false);
                var receipt = new PlatformResult(42, Guid.NewGuid(), [alice.Id], JsonSerializer.SerializeToElement(result));
                TestAssert.True(reopened.RecordResult(receipt));
                TestAssert.False(reopened.RecordResult(receipt with
                {
                    Result = JsonDocument.Parse("{\"winner\":\"Chaos\"}").RootElement.Clone()
                }), "the same host match must be recorded once");
                TestAssert.Equal(1, reopened.ResultsFor(alice.Id).Length);
            }
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Check("server.air-platform-sessions")]
    public static void SessionsFlowFromHttpTicketThroughFlexAuthAndExpire()
    {
        var root = TemporaryDirectory("league-air-sessions");
        GameManager? manager = null;
        try
        {
            using var accounts = new PlatformAccounts(root);
            var account = Register(accounts, "Alice", "Alice Session");
            var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
            var sessions = new PlatformSessions(accounts, clock);
            manager = CreateNonLaunchingManager(root);
            var services = new PlatformServices(accounts, sessions,
                new PlatformGames(accounts, manager, "127.0.0.1", clock));

            var ticket = sessions.IssueTicket(account);
            TestAssert.True(sessions.AccountForConnection("air-1") is null,
                "an HTTP ticket is not a Flex-authenticated session");
            var login = (AsObject)services.Route("air-1", "loginService", "login", [
                AirWire.Object("com.riotgames.platform.login.AuthenticationCredentials",
                    ("username", "Alice"), ("authToken", ticket), ("clientVersion", Version))
            ])!;
            TestAssert.Equal(account.Id, Convert.ToInt64(((AsObject)login["accountSummary"])["accountId"], CultureInfo.InvariantCulture));
            TestAssert.True(sessions.AccountForConnection("air-1") is null,
                "the remoting login does not implicitly channel-authenticate the socket");

            var sessionToken = (string)login["token"];
            var chatUsername = (string)((AsObject)login["accountSummary"])["username"];
            var chatPassword = "AIR_" + (string)login["password"];
            TestAssert.Equal(AirGameDtos.InternalName(account), chatUsername);
            TestAssert.True(sessions.AuthenticateChat(chatUsername, chatPassword) is null);
            TestAssert.False(sessions.AuthenticateChannel("air-1", "Alice", "wrong-token"));
            TestAssert.True(sessions.AuthenticateChannel("air-1", "alice", sessionToken));
            TestAssert.True(sessions.AuthenticateChannel("air-1", chatUsername, sessionToken));
            TestAssert.Equal(account.Id, sessions.AuthenticateChat(chatUsername, chatPassword));
            TestAssert.True(sessions.AuthenticateChat(chatUsername, "AIR_" + sessionToken) is null,
                "the chat credential is separate from the Flex token");
            TestAssert.Equal(account.Id, sessions.AccountForConnection("air-1"));

            var accountView = services.Route("air-1", "loginService", "getLoggedInAccountView", [sessionToken]);
            TestAssert.True(accountView is AsObject, "Flex-authenticated session must expose the account view");

            clock.Advance(TimeSpan.FromMinutes(29));
            services.Route("air-1", "loginService", "performLCDSHeartBeat", [0, "validate-lcds-connection-4-invite"]);
            clock.Advance(TimeSpan.FromMinutes(29));
            TestAssert.Equal(account.Id, sessions.Require("air-1").AccountId,
                "the stock heartbeat marker must extend a long testing session");
            clock.Advance(TimeSpan.FromMinutes(2));
            TestAssert.True(sessions.AccountForConnection("air-1") is null,
                "expired channel sessions must no longer resolve to an account");
            TestAssert.True(sessions.AuthenticateChat(chatUsername, chatPassword) is null);
            TestAssert.Throws<ArgumentException>(() => services.Route(
                "air-1", "loginService", "getLoggedInAccountView", [sessionToken]));

            var expiringTicket = sessions.IssueTicket(account);
            clock.Advance(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(1));
            TestAssert.Throws<ArgumentException>(() => sessions.Login("air-2", "alice", expiringTicket, Version));
        }
        finally
        {
            manager?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            DeleteDirectory(root);
        }
    }

    [Check("server.air-session-resume-proof-and-window")]
    public static void ParkedSessionResumesOnlyWithItsTokenWithinTheWindowAndOnce()
    {
        var root = TemporaryDirectory("league-air-session-resume");
        try
        {
            using var accounts = new PlatformAccounts(root);
            var account = Register(accounts, "Alice", "Alice Session");
            var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
            var sessions = new PlatformSessions(accounts, clock);
            var internalName = AirGameDtos.InternalName(account);

            string Open(string connection, bool channel = true)
            {
                var session = sessions.Login(connection, "alice", sessions.IssueTicket(account), Version);
                if (channel) TestAssert.True(sessions.AuthenticateChannel(connection, "alice", session.Token));
                return session.Token;
            }

            // The proof is the session token; an account name, a wrong token or a wrong name is not.
            var token = Open("air-1");
            sessions.Suspend("air-1");
            TestAssert.True(sessions.AccountForConnection("air-1") is null, "a parked session is bound to no connection");
            TestAssert.Throws<ArgumentException>(() => sessions.Require("air-1"));
            TestAssert.False(sessions.Resume("air-2", "alice", ""));
            TestAssert.False(sessions.Resume("air-2", "alice", "wrong-token"));
            TestAssert.False(sessions.Resume("air-2", "mallory", token));
            TestAssert.False(sessions.Resume("air-2", "alice", token + "x"));
            TestAssert.True(sessions.Resume("air-2", internalName, token), "the chat-style internal name is accepted like the channel login");
            TestAssert.Equal(account.Id, sessions.AccountForConnection("air-2"));
            TestAssert.True(sessions.AccountForConnection("air-1") is null, "the old binding stays dead");
            TestAssert.Equal(account.Id, sessions.AuthenticateChat(internalName, "AIR_" + sessions.Require("air-2").ChatToken),
                "the chat credential travels with the session");

            // One resume per park: a replay of the same credentials finds nothing.
            sessions.Suspend("air-2");
            TestAssert.True(sessions.Resume("air-3", "alice", token));
            TestAssert.False(sessions.Resume("air-4", "alice", token));

            // A connection that already has a session does not take another one over (its own login also ended the parked one).
            sessions.Suspend("air-3");
            Open("air-5");
            TestAssert.False(sessions.Resume("air-5", "alice", token));

            // The resume window is 60 minutes measured from the close (batch 436: a stock client that drops during a game sends
            // nothing until the game ends, so the former 120 s window refused its reconnect).
            token = Open("air-6");
            sessions.Suspend("air-6");
            clock.Advance(TimeSpan.FromMinutes(59));
            TestAssert.True(sessions.Resume("air-7", "alice", token), "inside the window, 59 minutes after the close");
            sessions.Suspend("air-7");
            clock.Advance(TimeSpan.FromMinutes(61));
            TestAssert.False(sessions.Resume("air-8", "alice", token), "past the window, 61 minutes after the close");

            // A newer login of the account supersedes every parked binding (the second login wins).
            token = Open("air-9");
            sessions.Suspend("air-9");
            Open("air-10");
            TestAssert.False(sessions.Resume("air-11", "alice", token));
            TestAssert.Equal(account.Id, sessions.AccountForConnection("air-10"));

            // Only a channel-authenticated session is parked, and a logout parks nothing.
            sessions.Logout("air-10");
            token = Open("air-12", channel: false);
            sessions.Suspend("air-12");
            TestAssert.False(sessions.Resume("air-13", "alice", token), "a session that never logged in on the channel");
            token = Open("air-14");
            sessions.Logout("air-14");
            sessions.Suspend("air-14");
            TestAssert.False(sessions.Resume("air-15", "alice", token), "an explicit logout");

            // A session parked past the resume window (60 minutes after its close) is gone, whatever its rolling expiry says.
            token = Open("air-16");
            sessions.Suspend("air-16");
            clock.Advance(TimeSpan.FromMinutes(61));
            TestAssert.False(sessions.Resume("air-17", "alice", token));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Check("server.air-platform-games")]
    public static void FourAccountLobbyKeepsTeamsAtomicAndFreezesLoadoutsAfterPostSelection()
    {
        var root = TemporaryDirectory("league-air-games");
        GameManager? manager = null;
        try
        {
            using (var accounts = new PlatformAccounts(Path.Combine(root, "accounts")))
            {
                var players = Enumerable.Range(1, 4)
                    .Select(index => Register(accounts, $"Player{index}", $"Player {index}"))
                    .ToArray();
                var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
                manager = CreateNonLaunchingManager(root);
                var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
                var pushes = new ConcurrentQueue<(long AccountId, object Body)>();
                games.Push = (accountId, body) => pushes.Enqueue((accountId, body));

                var created = AsObject(games.Create(players[0], "first", "pw", 1, 4,
                    AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE"));
                var gameId = Integer(created, "id");
                games.Join(players[1], gameId, "pw");
                games.Join(players[2], gameId, "pw");
                games.Join(players[3], gameId, "pw");
                var joined = AsObject(games.Get(players[0].Id, gameId));
                TestAssert.Equal(2, Count(joined["teamOne"]));
                TestAssert.Equal(2, Count(joined["teamTwo"]));

                var revision = Long(AsObject(games.Get(players[0].Id, gameId)), "optimisticLock");
                TestAssert.Throws<ArgumentException>(() => games.Start(players[1].Id, gameId, revision),
                    "only the owner may start champion selection");
                games.Quit(players[3].Id);
                var second = AsObject(games.Create(players[3], "second", "pw2", 1, 2,
                    AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE"));
                TestAssert.Equal(Integer(second, "id"), Integer(AsObject(games.Current(players[3].Id)!), "id"));

                revision = Long(AsObject(games.Get(players[0].Id, gameId)), "optimisticLock");
                games.Start(players[0].Id, gameId, revision);

                var contenders = new[] { players[0], players[2] };
                var sameTeam = contenders
                    .Select(player => (player, task: Task.Run(() => TryPick(games, player.Id, 1))))
                    .ToArray();
                Task.WaitAll(sameTeam.Select(item => item.task).ToArray());
                TestAssert.Equal(1, sameTeam.Count(item => item.task.Result),
                    "same-team champion picks must commit atomically");
                var winnerAccount = sameTeam.Single(item => item.task.Result).player;
                var otherTeamMate = contenders.Single(player => player.Id != winnerAccount.Id);
                games.Pick(otherTeamMate.Id, 61);
                games.Pick(players[1].Id, 1);
                TestAssert.Throws<ArgumentException>(() => games.SelectSkin(players[1].Id, 1, 6),
                    "AIR global skin IDs must not be treated as native mesh indexes");
                games.SelectSkin(players[1].Id, 1, 1006);
                var selected = AsObject(games.Get(players[0].Id, gameId));
                TestAssert.Equal(3, Count(selected["playerChampionSelections"]));
                var skins = ((IEnumerable)selected["playerChampionSelections"]).Cast<AsObject>();
                TestAssert.Equal(6, Integer(skins.Single(s => (string)s["summonerInternalName"] == AirGameDtos.InternalName(players[1])), "selectedSkinIndex"));
                TestAssert.Equal(9, Count(selected["accountSpellsMap"]));

                var frozenLoadout = accounts.Get(players[0].Id).Loadout with
                {
                    SummonerSpell1 = "SummonerHeal",
                    SummonerSpell2 = "SummonerDot"
                };
                games.SaveLoadout(players[0].Id, _ => frozenLoadout);
                games.LockIn(players[0].Id);
                games.SaveLoadout(players[0].Id,
                    current => current.Loadout with { SummonerSpell1 = "SummonerTeleport" });
                games.SelectRunePage(players[0].Id, players[0].SummonerId, 2);
                games.SaveRunePages(players[0].Id, players[0].SummonerId, accounts.GetRunePages(players[0].Id));
                var lockedSelection = ((IEnumerable)AsObject(games.Current(players[0].Id)!) ["playerChampionSelections"])
                    .Cast<AsObject>().Single(s => (string)s["summonerInternalName"] == AirGameDtos.InternalName(players[0]));
                var lockedChampion = Integer(lockedSelection, "championId");
                games.SelectSkin(players[0].Id, lockedChampion, AirGameDtos.SkinIds(lockedChampion).First());
                TestAssert.Throws<ArgumentException>(() => games.Pick(players[0].Id, 35),
                    "individual lock-in freezes champion identity, not the remaining loadout");
                games.LockIn(players[1].Id);
                games.LockIn(players[2].Id);
                games.SaveMasteries(players[0].Id, new Dictionary<int, int> { [103] = 1, [106] = 1 });
                games.SelectRunePage(players[0].Id, players[0].SummonerId, 1);
                games.SelectSpells(players[0].Id, AirGameDtos.SpellId("SummonerHeal"), AirGameDtos.SpellId("SummonerDot"));
                TestAssert.Equal(1, accounts.Get(players[0].Id).Loadout.Masteries![103]);
                games.SelectSkin(players[0].Id, lockedChampion, 0);
                var postPickSkin = ((IEnumerable)AsObject(games.Current(players[0].Id)!)["playerChampionSelections"])
                    .Cast<AsObject>().Single(s => (string)s["summonerInternalName"] == AirGameDtos.InternalName(players[0]));
                TestAssert.Equal(0, Integer(postPickSkin, "selectedSkinIndex"),
                    "stock skin controls remain active in the post-pick countdown");
                TestAssert.Throws<ArgumentException>(() => games.Pick(players[0].Id, 35));
                clock.Advance(TimeSpan.FromSeconds(3));
                games.SelectSkin(players[0].Id, lockedChampion, 0);
                clock.Advance(TimeSpan.FromSeconds(10));
                TestAssert.Throws<ArgumentException>(() => games.SelectSkin(players[0].Id, lockedChampion, 0),
                    "the countdown deadline freezes skins even before the next platform tick");
                TestAssert.Throws<ArgumentException>(() => games.SelectSpells(players[0].Id,
                    AirGameDtos.SpellId("SummonerTeleport"), AirGameDtos.SpellId("SummonerDot")),
                    "the countdown deadline freezes spell changes");
                TestAssert.True(accounts.Get(players[1].Id).Loadout.Masteries is null or { Count: 0 });
                games.SaveMasteries(players[1].Id, []);
                games.SaveMasteries(players[0].Id, new Dictionary<int, int> { [106] = 1, [103] = 1 });
                // Item 33: mastery and rune pages are account data, not match fields. The stock client leaves its Save
                // controls live at "starting game", and changeTalentRankings/saveSpellBook carry no error handler, so a
                // refusal there is the generic PvP.net error window. They save; the match roster stays the launch snapshot.
                games.SaveMasteries(players[0].Id, new Dictionary<int, int> { [103] = 2, [106] = 1 });
                TestAssert.Equal(2, accounts.Get(players[0].Id).Loadout.Masteries![103],
                    "a mastery page saved after the countdown is kept for the next match");
                var sessions = new PlatformSessions(accounts);
                var session = sessions.Login("late-masteries", players[0].Username,
                    sessions.IssueTicket(players[0]), Version);
                TestAssert.True(sessions.AuthenticateChannel("late-masteries", players[0].Username, session.Token));
                var rpc = new PlatformServices(accounts, sessions, games);
                var talents = (AsObject)rpc.Route("late-masteries", "summonerService", "changeTalentRankings",
                    [(double)players[0].SummonerId, new Dictionary<string, object> { ["106"] = 1, ["103"] = 1 }])!;
                TestAssert.Equal("com.riotgames.platform.summoner.SummonerTalentsAndPoints", talents.TypeName,
                    "the stock handler casts the reply, so a save after the countdown answers the talent record");
                TestAssert.Equal(1, accounts.Get(players[0].Id).Loadout.Masteries![103]);
                games.SelectRunePage(players[0].Id, players[0].SummonerId, 2);
                games.SaveRunePages(players[0].Id, players[0].SummonerId, accounts.GetRunePages(players[0].Id));
                TestAssert.Equal(2L, accounts.GetRunePages(players[0].Id).Single(page => page.IsCurrent).PageId,
                    "a rune page saved after the countdown is kept for the next match");
                TestAssert.Throws<ArgumentException>(() => games.SaveLoadout(players[0].Id,
                    current => current.Loadout with { SummonerSpell1 = "SummonerTeleport" }));

                TestAssert.Equal("SummonerHeal", accounts.Get(players[0].Id).Loadout.SummonerSpell1);
                TestAssert.Equal("SummonerDot", accounts.Get(players[0].Id).Loadout.SummonerSpell2);
                TestAssert.True(pushes.Any(push => push.AccountId == players[0].Id),
                    "quitting the first lobby must publish its changed roster to remaining members");
            }
        }
        finally
        {
            manager?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            DeleteDirectory(root);
        }
    }

    [Check("server.air-lobby-departure-contract")]
    public static void KickTransferAndExpiryUseStockNotificationsAndReleaseMembership()
    {
        var root = TemporaryDirectory("league-air-departure");
        GameManager? manager = null;
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            var owner = Register(accounts, "owner", "Owner");
            var removed = Register(accounts, "removed", "Removed");
            var successor = Register(accounts, "successor", "Successor");
            var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
            manager = CreateNonLaunchingManager(root);
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
            var pushes = new List<(long Account, AsObject Body)>();
            games.Push = (account, body) => pushes.Add((account, AsObject(body)));
            var lobby = AsObject(games.Create(owner, "Departure", "", 1, 4, 1, "CLASSIC", "NONE"));
            var id = Integer(lobby, "id");
            games.Join(removed, id, "");
            games.Join(successor, id, "");
            TestAssert.Throws<ArgumentException>(() => games.Kick(removed.Id, id, successor.Id));
            pushes.Clear();
            games.Kick(owner.Id, id, removed.Id);
            var kicked = pushes.Single(p => p.Account == removed.Id).Body;
            TestAssert.Equal("com.riotgames.platform.game.message.GameNotification", kicked.TypeName);
            TestAssert.Equal("PLAYER_BANNED_FROM_GAME", kicked["type"]);
            TestAssert.True(games.Current(removed.Id) is null);
            TestAssert.Throws<ArgumentException>(() => games.Join(removed, id, ""));
            TestAssert.Equal("TEAM_SELECT", AsObject(games.Current(successor.Id)!)["gameState"]);

            games.Quit(owner.Id);
            var transferred = AsObject(games.Current(successor.Id)!);
            TestAssert.Equal((double)successor.Id, AsObject(transferred["ownerSummary"])["accountId"]);
            games.Create(removed, "Another lobby", "", 1, 2, 1, "CLASSIC", "NONE");
            pushes.Clear();
            clock.Advance(TimeSpan.FromMinutes(31));
            games.Tick();
            var expired = pushes.Single(p => p.Account == successor.Id).Body;
            TestAssert.Equal("TERMINATED", expired["gameState"]);
            TestAssert.Equal("LOBBY_EXPIRED", expired["terminatedCondition"]);
            TestAssert.Equal("LOBBY_EXPIRED", AsObject(games.Get(successor.Id, id))["terminatedCondition"]);
            TestAssert.True(games.Current(successor.Id) is null);
            TestAssert.True(games.Current(removed.Id) is null);
            TestAssert.Equal(0, Count(games.List()));
            var count = pushes.Count;
            games.Tick();
            TestAssert.Equal(count, pushes.Count, "expired lobbies are not re-published every tick");
            TestAssert.True(Integer(AsObject(games.Create(successor, "Retry", "", 1, 2, 1, "CLASSIC", "NONE")), "id") > id);
        }
        finally
        {
            manager?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            DeleteDirectory(root);
        }
    }

    // Playtest B2 YV1, 26 Sept 2026: a lobby kept each member's icon from join time.
    [Check("server.air-lobby-profile-icon-push")]
    public static void ProfileIconChangeRepublishesTheLobby()
    {
        var root = TemporaryDirectory("league-air-icon");
        GameManager? manager = null;
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            var owner = Register(accounts, "iconowner", "Icon Owner");
            var guest = Register(accounts, "iconguest", "Icon Guest");
            manager = CreateNonLaunchingManager(root);
            var games = new PlatformGames(accounts, manager, "127.0.0.1");
            var pushes = new List<(long Account, AsObject Body)>();
            games.Push = (account, body) => pushes.Add((account, AsObject(body)));
            var id = Integer(AsObject(games.Create(owner, "Icons", "", 1, 2, 1, "CLASSIC", "NONE")), "id");
            games.Join(guest, id, "");
            var before = Long(AsObject(games.Get(owner.Id, id)), "optimisticLock");
            pushes.Clear();

            var sessions = new PlatformSessions(accounts);
            var session = sessions.Login("icon-guest", guest.Username, sessions.IssueTicket(guest), Version);
            TestAssert.True(sessions.AuthenticateChannel("icon-guest", guest.Username, session.Token));
            var rpc = new PlatformServices(accounts, sessions, games);
            _ = rpc.Route("icon-guest", "summonerService", "updateProfileIconId", [7d]);

            int GuestIcon(AsObject game) => Integer(((IEnumerable)game["teamOne"]).Cast<AsObject>()
                .Concat(((IEnumerable)game["teamTwo"]).Cast<AsObject>())
                .Single(member => Long(member, "accountId") == guest.Id), "profileIconId");
            foreach (var member in new[] { owner.Id, guest.Id })
            {
                var dto = pushes.Single(push => push.Account == member).Body;
                TestAssert.Equal("com.riotgames.platform.game.GameDTO", dto.TypeName);
                TestAssert.True(Long(dto, "optimisticLock") > before, "the icon change bumps the lobby revision");
                TestAssert.Equal(7, GuestIcon(dto), "every member receives the guest's new icon");
            }
            TestAssert.Equal(7, GuestIcon(AsObject(games.Get(owner.Id, id))));
        }
        finally
        {
            manager?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            DeleteDirectory(root);
        }
    }

    private static bool TryPick(PlatformGames games, long accountId, int champion)
    {
        try
        {
            // A champion a teammate holds comes back as the unchanged game, so success is the player's own selection.
            var game = (AsObject)games.Pick(accountId, champion);
            return ((IEnumerable)game["playerChampionSelections"]).Cast<AsObject>().Any(selection =>
                Equals(selection["championId"], champion) &&
                ((string)selection["summonerInternalName"]).EndsWith((1_000_000 + accountId).ToString(), StringComparison.Ordinal));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static PlatformAccount Register(PlatformAccounts accounts, string username, string summoner) =>
        accounts.RegisterPublic(username, "secret-password", summoner);

    private static AsObject AsObject(object value) => value as AsObject
        ?? throw new InvalidOperationException("Expected a typed AIR object.");

    private static int Integer(AsObject value, string field) =>
        Convert.ToInt32(value[field], CultureInfo.InvariantCulture);

    private static long Long(AsObject value, string field) =>
        Convert.ToInt64(value[field], CultureInfo.InvariantCulture);

    private static int Count(object value) => value is IEnumerable values ? values.Cast<object>().Count() : -1;

    internal static GameManager CreateNonLaunchingManager(string root)
    {
        var dataRoot = Path.Combine(root, "game-data");
        Directory.CreateDirectory(dataRoot);
        var executable = Path.Combine(root, "server-placeholder.exe");
        File.WriteAllBytes(executable, []);
        return new GameManager(new HostSettings(dataRoot, executable,
            FirstGamePort: EphemeralPort(), MaxGames: 1, BindAddress: "127.0.0.1"), Path.Combine(root, "games"));
    }

    private static int EphemeralPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string TemporaryDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset now = current;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
}
