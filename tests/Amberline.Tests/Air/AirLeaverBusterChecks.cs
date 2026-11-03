using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;
using static Amberline.Tests.Air.AirQueueChecks;

namespace Amberline.Tests.Air;

/// <summary>Batch 587: LeaverBuster, the leave count, the warning level, the low-priority queue wait and the host's Clear.</summary>
public static class AirLeaverBusterChecks
{
    [Check("server.air-leaverbuster-counts-matchmade-leaves-and-forgives-reconnects")]
    public static void CountsMatchmadeLeavesAndForgivesReconnects() => CountsAsync().GetAwaiter().GetResult();

    [Check("server.air-leaverbuster-low-priority-wait-delays-the-player-and-the-party")]
    public static void LowPriorityWaitDelaysThePlayerAndTheParty() => WaitAsync().GetAwaiter().GetResult();

    [Check("server.air-leaverbuster-host-page-shows-and-clears")]
    public static void HostPageShowsAndClears() => HostPageAsync().GetAwaiter().GetResult();

    private static PlayerMatchResult Gone(PlayerMatchResult player) => player with { Leaver = true, Connected = false };

    // One finished game an hour after the last, in the queue given (0 is a custom game).
    private static void Play(PlatformAccounts accounts, PlatformAccount account, ManualClock clock, int queue,
        Func<PlayerMatchResult, PlayerMatchResult>? how = null)
    {
        clock.Now = clock.Now.AddHours(1);
        var player = how is null ? Player(account) : how(Player(account));
        var match = new MatchResult(8, "Version 1.0.0.126", 1, "CLASSIC", "Order", 360, [player], false);
        TestAssert.True(accounts.RecordResult(new(accounts.AllocateGameId(), Guid.NewGuid(), [account.Id],
            JsonSerializer.SerializeToElement(match, MatchRoster.JsonOptions), clock.Now, queue)));
    }

    private static async Task CountsAsync()
    {
        var root = TemporaryDirectory("league-air-leaverbuster-counts");
        try
        {
            var clock = new ManualClock();
            var directory = Path.Combine(root, "accounts");
            using (var accounts = new PlatformAccounts(directory, timeProvider: clock))
            {
                var account = accounts.RegisterPublic("leavecount", "testing-password", "Leave Count");
                PlatformLeaverStatus Status() => accounts.LeaverStatus(account.Id);

                Play(accounts, account, clock, 0, Gone);
                Play(accounts, account, clock, PlatformGames.NormalQueueId, p => p with { Leaver = true, Connected = true });
                TestAssert.Equal(new PlatformLeaverStatus(0, 1, 0, TimeSpan.Zero, 0), Status(),
                    "a custom game never counts, and a disconnect leaver who came back and finished is forgiven");
                Play(accounts, account, clock, PlatformGames.NormalQueueId, Gone);
                TestAssert.Equal(new PlatformLeaverStatus(1, 2, 1, TimeSpan.Zero, 0), Status(), "one leave is a warning only");

                // The stock login packet carries the level; the client shows leaver_penalty_warning_1 for it.
                var sessions = new PlatformSessions(accounts);
                var session = sessions.Login("rtmp-leaver", account.Username, sessions.IssueTicket(account), AirQueueChecks.Version);
                TestAssert.True(sessions.AuthenticateChannel("rtmp-leaver", account.Username, session.Token));
                await using (var manager = NonLaunchingManager(root))
                {
                    var rpc = new PlatformServices(accounts, sessions, new PlatformGames(accounts, manager, "127.0.0.1", clock));
                    var login = (AsObject)rpc.Route("rtmp-leaver", "clientFacadeService", "getLoginDataPacketForUser", [])!;
                    TestAssert.Equal(1, login["leaverPenaltyLevel"]);
                    TestAssert.Equal(0, login["leaverBusterPenaltyTime"], "the alternate-login guard stays off");
                }

                // Latched AFK counts even while connected (an AFK kick is a leaver), in Co-op vs. AI too: level 2 waits 5 minutes.
                Play(accounts, account, clock, PlatformGames.CoopVsAiQueueId, p => p with { WasAfk = true });
                TestAssert.Equal(new PlatformLeaverStatus(2, 3, 2, TimeSpan.FromMinutes(5), 5), Status());
                for (var game = 0; game < 4; game++) Play(accounts, account, clock, PlatformGames.NormalQueueId);
                TestAssert.Equal(new PlatformLeaverStatus(2, 7, 2, TimeSpan.FromMinutes(5), 1), Status(), "each completed game works one off");
                Play(accounts, account, clock, PlatformGames.NormalQueueId);
                TestAssert.Equal(new PlatformLeaverStatus(2, 8, 2, TimeSpan.Zero, 0), Status(), "five completed games end the wait, not the warning");
                Play(accounts, account, clock, 0);
                TestAssert.Equal(8, Status().Games, "custom games neither count nor decay");

                // The window is the last 20 matchmade games: the leaves fall out of it.
                for (var game = 0; game < 13; game++) Play(accounts, account, clock, PlatformGames.NormalQueueId);
                TestAssert.Equal(new PlatformLeaverStatus(2, 20, 2, TimeSpan.Zero, 0), Status());
                Play(accounts, account, clock, PlatformGames.NormalQueueId);
                TestAssert.Equal(1, Status().Leaves);
                Play(accounts, account, clock, PlatformGames.NormalQueueId);
                TestAssert.Equal(new PlatformLeaverStatus(0, 20, 0, TimeSpan.Zero, 0), Status());

                // Repeat leaves escalate to 10 and 15 minutes; the level stops at the client's last warning, 4.
                var repeat = accounts.RegisterPublic("leaverepeat", "testing-password", "Leave Repeat");
                TimeSpan[] waits = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10),
                    TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15)];
                for (var leaves = 1; leaves <= 5; leaves++)
                {
                    Play(accounts, repeat, clock, PlatformGames.NormalQueueId, Gone);
                    var status = accounts.LeaverStatus(repeat.Id);
                    TestAssert.True(status.Leaves == leaves && status.Level == Math.Min(leaves, 4) && status.QueueDelay == waits[leaves],
                        $"after {leaves} leaves: {status}");
                }

                clock.Now = clock.Now.AddMinutes(1);
                accounts.ClearLeaves(repeat.Id);
                TestAssert.Equal(new PlatformLeaverStatus(0, 0, 0, TimeSpan.Zero, 0), accounts.LeaverStatus(repeat.Id));
            }
            using (var accounts = new PlatformAccounts(directory, timeProvider: clock))
            {
                var repeat = accounts.FindName("Leave Repeat")!;
                TestAssert.Equal(0, accounts.LeaverStatus(repeat.Id).Leaves, "the clear is saved with the account");
                Play(accounts, repeat, clock, PlatformGames.NormalQueueId, Gone);
                TestAssert.Equal(1, accounts.LeaverStatus(repeat.Id).Leaves, "a leave after the clear counts again");
            }
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task WaitAsync()
    {
        var root = TemporaryDirectory("league-air-leaverbuster-wait");
        try
        {
            var clock = new ManualClock();
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"), timeProvider: clock);
            await using var manager = NonLaunchingManager(root);
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock);
            var pushed = new List<(long Account, AsObject Body)>();
            games.Push = (account, body) => pushed.Add((account, (AsObject)body));
            var connections = new Dictionary<string, long>();
            games.AccountForConnection = connection => connections.GetValueOrDefault(connection);
            // Each account's connection is its user name.
            PlatformAccount NewPlayer(string name)
            {
                var account = accounts.RegisterPublic(name, "testing-password", name);
                connections[account.Username] = account.Id;
                return account;
            }
            PlatformAccount Penalised(string name)
            {
                var account = NewPlayer(name);
                Play(accounts, account, clock, PlatformGames.NormalQueueId, Gone);
                Play(accounts, account, clock, PlatformGames.NormalQueueId, Gone);
                return account;
            }
            static int Approx(object reply) => (int)((AsObject)((ArrayCollection)((AsObject)reply)["joinedQueues"])[0])["waitTime"];
            const int TreelineQueue = PlatformGames.TwistedTreelineQueueId;

            // A level-2 leaver queues first, then five clean players: none of them can be matched before the 5 minutes are up.
            var leaver = Penalised("lowprio");
            TestAssert.True(Approx(games.AttachQueue(leaver, leaver.Username, [TreelineQueue])) >= 300_000,
                "the ribbon's Approx wait shows the delay");
            games.ConfirmQueueReply(leaver.Username);
            var others = Enumerable.Range(0, 5).Select(index => NewPlayer("clean" + index)).ToArray();
            foreach (var other in others)
            {
                TestAssert.True(Approx(games.AttachQueue(other, other.Username, [TreelineQueue])) < 300_000, "a clean player has no delay");
                games.ConfirmQueueReply(other.Username);
            }
            games.Tick();
            TestAssert.True(games.Current(leaver.Id) is null && games.Current(others[0].Id) is null, "five matchable players are not 3v3");
            clock.Now = clock.Now.AddMinutes(5).AddSeconds(-1);
            games.Tick();
            TestAssert.True(games.Current(leaver.Id) is null, "still waiting a second before the delay ends");
            clock.Now = clock.Now.AddSeconds(1);
            games.Tick();
            TestAssert.True(games.Current(leaver.Id) is not null && others.All(other => games.Current(other.Id) is not null),
                "once the delay is over the leaver is matched");

            // A party waits out its member's delay: the clean leader, the penalised member and four solo players.
            var leader = NewPlayer("partyclean");
            var member = Penalised("partylowprio");
            games.AcceptInvite(member, member.Username, "9001");
            var solos = Enumerable.Range(0, 4).Select(index => NewPlayer("solo" + index)).ToArray();
            foreach (var solo in solos)
            {
                games.AttachQueue(solo, solo.Username, [TreelineQueue]);
                games.ConfirmQueueReply(solo.Username);
            }
            var reply = games.AttachQueue(leader, leader.Username, [TreelineQueue], team: [leader.SummonerId, member.SummonerId], invitationId: "9001");
            TestAssert.True(Approx(reply) >= 300_000, "the leader's Approx carries the member's delay");
            TestAssert.True(pushed.Any(push => push.Account == member.Id && Approx(push.Body) >= 300_000), "and so does the member's");
            games.ConfirmQueueReply(leader.Username);
            games.Tick();
            TestAssert.True(games.Current(leader.Id) is null && games.Current(solos[0].Id) is null, "the party is not matchable yet");
            clock.Now = clock.Now.AddMinutes(5);
            games.Tick();
            var game = (AsObject)games.Current(leader.Id)!;
            TestAssert.True(game is not null && games.Current(member.Id) is not null && solos.All(solo => games.Current(solo.Id) is not null),
                "after the delay the party and the solo players form the game");
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task HostPageAsync()
    {
        var root = TemporaryDirectory("league-air-leaverbuster-page");
        try
        {
            var clock = new ManualClock();
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"), timeProvider: clock);
            var leaver = accounts.RegisterPublic("pageleaver", "testing-password", "Page Leaver");
            var clean = accounts.RegisterPublic("pageclean", "testing-password", "Page Clean");
            Play(accounts, leaver, clock, PlatformGames.NormalQueueId, Gone);
            Play(accounts, leaver, clock, PlatformGames.NormalQueueId, p => p with { WasAfk = true });
            Play(accounts, clean, clock, PlatformGames.NormalQueueId);
            clock.Now = clock.Now.AddMinutes(1);

            var port = AirManagementChecks.FreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
            builder.Services.AddAntiforgery();
            await using var app = builder.Build();
            PlatformManagement.Map(app, accounts, port, AirManagementChecks.FreePort());
            await app.StartAsync();
            try
            {
                using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer(), UseProxy = false };
                using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
                var page = await client.GetStringAsync("/manage");
                TestAssert.True(page.Contains("<td>Page Leaver</td><td>2 of 2</td><td>level 2</td><td>5 min for 5 more games</td>", StringComparison.Ordinal),
                    "the leaver's row shows the leaves, the warning and the wait");
                TestAssert.True(page.Contains("<td>Page Clean</td><td>0 of 1</td><td>none</td><td>none</td><td>none</td><td></td>", StringComparison.Ordinal),
                    "a clean account has no Clear button");
                var field = Regex.Match(page, "name=\"([^\"]+)\" value=\"([^\"]+)\"");
                var token = new KeyValuePair<string, string>(field.Groups[1].Value, WebUtility.HtmlDecode(field.Groups[2].Value));
                FormUrlEncodedContent Clear(bool withToken = true)
                {
                    var fields = new Dictionary<string, string> { ["accountId"] = leaver.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                    if (withToken) fields[token.Key] = token.Value;
                    return new FormUrlEncodedContent(fields);
                }

                using var forged = await client.PostAsync("/manage/leavers/clear", Clear(withToken: false));
                TestAssert.False(forged.IsSuccessStatusCode || forged.StatusCode == HttpStatusCode.Redirect, "Clear needs its antiforgery token");
                using var remote = new HttpRequestMessage(HttpMethod.Post, "/manage/leavers/clear") { Content = Clear() };
                remote.Headers.Host = "public.example";
                TestAssert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(remote)).StatusCode, "a rebound host name is refused");
                TestAssert.Equal(2, accounts.LeaverStatus(leaver.Id).Leaves, "no refused post clears anything");

                using var cleared = await client.PostAsync("/manage/leavers/clear", Clear());
                TestAssert.Equal(HttpStatusCode.Redirect, cleared.StatusCode);
                TestAssert.Equal(new PlatformLeaverStatus(0, 0, 0, TimeSpan.Zero, 0), accounts.LeaverStatus(leaver.Id));
                TestAssert.Equal(1, accounts.LeaverStatus(clean.Id).Games, "only the chosen account is cleared");
                page = await client.GetStringAsync("/manage?refresh=off");
                TestAssert.True(page.Contains("<td>Page Leaver</td><td>0 of 0</td><td>none</td><td>none</td><td>none</td><td></td>", StringComparison.Ordinal));
            }
            finally { await app.StopAsync(); }
        }
        finally { DeleteDirectory(root); }
    }
}
