using System.Net;
using System.Text.RegularExpressions;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;
using static Amberline.Tests.Air.AirQueueChecks;

namespace Amberline.Tests.Air;

/// <summary>Batch 589: the champion-select dodge timer (the client's QueueDodger join failure and QueueDodgeDialog).</summary>
public static class AirDodgeTimerChecks
{
    private const string DodgerAlias = "com.riotgames.platform.matchmaking.QueueDodger";

    [Check("server.air-dodge-champion-select-leaver-waits-and-keeps-the-party-out")]
    public static void ChampionSelectLeaverWaitsAndKeepsThePartyOut() => LeaverAsync().GetAwaiter().GetResult();

    [Check("server.air-dodge-timer-escalates-within-a-day")]
    public static void TimerEscalatesWithinADay() => Escalates();

    [Check("server.air-dodge-host-page-shows-and-clears")]
    public static void HostPageShowsAndClears() => HostPageAsync().GetAwaiter().GetResult();

    private static AsObject[] Failures(AsObject reply) => ((ArrayCollection)reply["playerJoinFailures"]).Cast<AsObject>().ToArray();

    private static async Task LeaverAsync()
    {
        var root = TemporaryDirectory("league-air-dodge-leaver");
        try
        {
            await using var host = new AirPartyQueueChecks.Rig(root);
            var players = Enumerable.Range(0, 10).Select(index => host.SignIn("dodger" + index)).ToArray();
            foreach (var player in players) host.Queue(player, PlatformGames.NormalQueueId);
            host.Games.Tick();
            int GameId(PlatformAccount player) => (int)(double)((AsObject)host.Games.Current(player.Id)!)["id"];
            PlatformDodgeStatus Status(PlatformAccount player) => host.Accounts.DodgeStatus(player.Id, host.Clock.Now);

            // A leaver whose champion grid never loaded (no CHAMP_SELECT_CLIENT) and who chose no champion is spared, as the client's
            // championSelection_championsNotLoaded promises. The nine others are back in the queue.
            host.Games.Quit(players[2].Id);
            TestAssert.Equal(new PlatformDodgeStatus(0, TimeSpan.Zero), Status(players[2]));
            TestAssert.Equal(9, ((AsObject)host.Games.QueueInfo())["queueLength"]);

            // A leaver who chose a champion dodged: six minutes.
            host.Queue(host.SignIn("dodgerlate1"), PlatformGames.NormalQueueId);
            host.Games.Tick();
            host.Games.Pick(players[1].Id, 1);
            host.Games.Quit(players[1].Id);
            TestAssert.Equal(new PlatformDodgeStatus(1, PlatformAccounts.FirstDodgeWait), Status(players[1]));

            // Every queue refuses the dodger with the client's QueueDodger row; nothing joins.
            foreach (var (queue, difficulty) in new[] { (PlatformGames.NormalQueueId, (string?)null), (PlatformGames.CoopVsAiQueueId, "EASY") })
            {
                var refused = Failures(host.Attach(players[1], "attachToQueue", queue, null, null, difficulty)).Single();
                TestAssert.Equal(DodgerAlias, refused.TypeName, "handleFailedJoinPlayers opens QueueDodgeDialog only for this class");
                TestAssert.Equal("QUEUE_DODGER", refused["reasonFailed"]);
                TestAssert.Equal(360_000d, refused["dodgePenaltyRemainingTime"], "milliseconds, which the dialog counts down");
                TestAssert.Equal((double)players[1].SummonerId, ((AsObject)refused["summoner"])["sumId"]);
            }
            TestAssert.Equal(9, ((AsObject)host.Games.QueueInfo())["queueLength"], "the dodger did not join");
            host.Clock.Now = host.Clock.Now.Add(PlatformAccounts.FirstDodgeWait).AddSeconds(-1);
            TestAssert.Equal(1000d, Failures(host.Attach(players[1], "attachToQueue", PlatformGames.NormalQueueId, null, null)).Single()
                ["dodgePenaltyRemainingTime"]);
            host.Clock.Now = host.Clock.Now.AddSeconds(1);
            TestAssert.True(host.Attach(players[1], "attachToQueue", PlatformGames.CoopVsAiQueueId, null, null, "EASY")["playerJoinFailures"] is null,
                "the timer is over");
            TestAssert.Equal(true, host.Rpc.Route(players[1].Username, "matchmakerService", "cancelFromQueueIfPossible", [7d]));

            // The AFK drop at the pick deadline is a dodge for a player whose grid loaded (the client's CHAMP_SELECT_CLIENT ack);
            // the others, who never acknowledged and chose nothing, are spared.
            host.Queue(host.SignIn("dodgerlate2"), PlatformGames.NormalQueueId);
            host.Games.Tick();
            TestAssert.True(host.Rpc.Route(players[0].Username, "gameService", "setClientReceivedGameMessage",
                [(double)GameId(players[0]), "CHAMP_SELECT_CLIENT"]) is null);
            host.Clock.Now = host.Clock.Now.AddSeconds(PlatformGames.MainPickServerSeconds);
            host.Games.Tick();
            TestAssert.True(host.Games.Current(players[0].Id) is null);
            TestAssert.Equal(new PlatformDodgeStatus(1, PlatformAccounts.FirstDodgeWait), Status(players[0]));
            TestAssert.Equal(new PlatformDodgeStatus(0, TimeSpan.Zero), Status(players[3]));

            // A party with a dodger cannot queue: one row for the member, "Because {0} abandoned a recent game during champion selection,
            // your team is unable to join the queue."
            var leader = host.SignIn("dodgerlead");
            _ = host.Rpc.Route(players[0].Username, "matchmakerService", "acceptInviteForMatchmakingGame", ["589"]);
            var party = Failures(host.Attach(leader, "attachTeamToQueue", PlatformGames.NormalQueueId,
                [(double)leader.SummonerId, (double)players[0].SummonerId], "589"));
            TestAssert.Equal(1, party.Length);
            TestAssert.Equal(DodgerAlias, party[0].TypeName);
            TestAssert.Equal((double)players[0].SummonerId, ((AsObject)party[0]["summoner"])["sumId"]);
            TestAssert.Equal(0, ((AsObject)host.Games.QueueInfo())["queueLength"], "neither the leader nor the member joined");
        }
        finally { DeleteDirectory(root); }
    }

    private static void Escalates()
    {
        var root = TemporaryDirectory("league-air-dodge-escalation");
        try
        {
            var clock = new ManualClock();
            var directory = Path.Combine(root, "accounts");
            var start = clock.Now;
            using (var accounts = new PlatformAccounts(directory, timeProvider: clock))
            {
                var account = accounts.RegisterPublic("dodgerepeat", "testing-password", "Dodge Repeat");
                accounts.RecordDodge(account.Id, start);
                accounts.RecordDodge(account.Id, start.AddMinutes(10));
                TestAssert.Equal(new PlatformDodgeStatus(2, PlatformAccounts.RepeatDodgeWait), accounts.DodgeStatus(account.Id, start.AddMinutes(10)),
                    "a repeat within 24 hours waits 30 minutes");
                accounts.RecordDodge(account.Id, start.AddHours(24));
                TestAssert.Equal(new PlatformDodgeStatus(2, PlatformAccounts.RepeatDodgeWait), accounts.DodgeStatus(account.Id, start.AddHours(24)),
                    "the first fell out of the day, the second did not");
                TestAssert.Equal(TimeSpan.FromMinutes(1), accounts.DodgeStatus(account.Id, start.AddHours(24).AddMinutes(29)).Remaining);
                accounts.RecordDodge(account.Id, start.AddHours(60));
                TestAssert.Equal(new PlatformDodgeStatus(1, PlatformAccounts.FirstDodgeWait), accounts.DodgeStatus(account.Id, start.AddHours(60)),
                    "a dodge a day after the last is a first again");
                TestAssert.Equal(new PlatformDodgeStatus(0, TimeSpan.Zero), accounts.DodgeStatus(account.Id, start.AddHours(84)));
            }
            using (var accounts = new PlatformAccounts(directory, timeProvider: clock))
                TestAssert.Equal(new PlatformDodgeStatus(1, PlatformAccounts.FirstDodgeWait),
                    accounts.DodgeStatus(accounts.FindName("Dodge Repeat")!.Id, start.AddHours(60)), "dodges are saved with the account");
        }
        finally { DeleteDirectory(root); }
    }

    private static async Task HostPageAsync()
    {
        var root = TemporaryDirectory("league-air-dodge-page");
        try
        {
            var clock = new ManualClock();
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"), timeProvider: clock);
            var dodger = accounts.RegisterPublic("pagedodger", "testing-password", "Page Dodger");
            accounts.RecordDodge(dodger.Id, clock.Now.AddMinutes(-1));

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
                TestAssert.True(page.Contains("<td>Page Dodger</td><td>0 of 0</td><td>none</td><td>none</td><td>1 in 24 h, 5:00 left</td><td><form", StringComparison.Ordinal),
                    "a dodger without leaves has the timer and the Clear button");
                var field = Regex.Match(page, "name=\"([^\"]+)\" value=\"([^\"]+)\"");
                using var cleared = await client.PostAsync("/manage/leavers/clear", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["accountId"] = dodger.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    [field.Groups[1].Value] = WebUtility.HtmlDecode(field.Groups[2].Value),
                }));
                TestAssert.Equal(HttpStatusCode.Redirect, cleared.StatusCode);
                TestAssert.Equal(new PlatformDodgeStatus(0, TimeSpan.Zero), accounts.DodgeStatus(dodger.Id));
                page = await client.GetStringAsync("/manage?refresh=off");
                TestAssert.True(page.Contains("<td>Page Dodger</td><td>0 of 0</td><td>none</td><td>none</td><td>none</td><td></td>", StringComparison.Ordinal));
            }
            finally { await app.StopAsync(); }
        }
        finally { DeleteDirectory(root); }
    }
}
