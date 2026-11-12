using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Data;
using Amberline.Host;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RtmpSharp.IO;
using static Amberline.Tests.Air.HostActivityChecks;
using static Amberline.Tests.Air.HostInviteChecks;
using static Amberline.Tests.Air.HostModeChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// The host's Users page and what its actions change (batch 748, task 4 of the host pages redesign): the account store's Last seen and Delete,
/// then the page. Made-up logins (<c>host_one</c>, <c>user1</c>, ...), temporary folders and free loopback ports; the match checks run this
/// repo's own match server and stop it before they end. No browser and no stock client run.
/// </summary>
public static class HostUsersChecks
{
    private const string Password = "Synthetic-users-Pw-48";
    private const string TestHost = "amberline.example.test";
    private const string ClientVersion = "1.45.11_10_04_15_28";
    private const string Users = HostInvite.UsersPath;

    /// <summary>
    /// The store: Last seen and the sign-up mark are written only when set; Touch writes at most once an hour; Delete refuses while a match
    /// handed to the match server names the login without a result (unless that match no longer runs), and then removes the login, frees its
    /// names and keeps its history (results, reports, a redeemed reward code) under the names it had, also after the store is read again.
    /// The social store loses the login's rows both ways.
    /// </summary>
    [Check("server.host-users-account-store")]
    public static void TheStoreStampsLastSeenHourlyAndDeletesALoginButNotItsHistory()
    {
        var root = Directory.CreateTempSubdirectory("league-host-users-store").FullName;
        try
        {
            var time = new Clock { Now = new DateTimeOffset(2026, 11, 11, 9, 5, 0, TimeSpan.Zero) };
            var folder = Path.Combine(root, "accounts");
            var file = Path.Combine(folder, "accounts.json");
            long goneId, goneSummonerId, keptId, hostId;
            Guid resultGame;
            using (var accounts = new PlatformAccounts(folder, timeProvider: time))
            {
                var host = accounts.RegisterPublic("host_one", Password, "Host One");
                var kept = accounts.RegisterPublic("self1", Password, "Self One", signedUp: true);
                var gone = accounts.RegisterPublic("gone1", Password, "Gone One");
                (hostId, keptId, goneId, goneSummonerId) = (host.Id, kept.Id, gone.Id, gone.SummonerId);
                var stored = File.ReadAllText(file);
                TestAssert.False(stored.Contains("\"LastSeen\"", StringComparison.Ordinal) || stored.Contains("\"Deleted\"", StringComparison.Ordinal),
                    "nothing new is written before it is set, so an older build still reads the store: " + stored);
                TestAssert.Equal(1, Regex.Count(stored, "\"SignedUp\": true"), "only the signed-up login carries the mark: " + stored);
                TestAssert.True(accounts.Find(kept.Id)!.SignedUp && !accounts.Find(gone.Id)!.SignedUp, "the sign-up mark reads back");

                // Touch: the page has a login's time at once (final fix 6: the file it is kept in, and its hourly writes, are
                // server.host-users-last-seen-file's), and never an older time over a newer one.
                accounts.Touch(gone.Id, time.Now);
                TestAssert.Equal(time.Now, accounts.LastSeen(gone.Id), "the first login is stamped");
                accounts.Touch(gone.Id, time.Now.AddMinutes(56));
                TestAssert.Equal(time.Now.AddMinutes(56), accounts.LastSeen(gone.Id), "a later login is stamped");
                accounts.Touch(gone.Id, time.Now.AddMinutes(50));
                TestAssert.Equal(time.Now.AddMinutes(56), accounts.LastSeen(gone.Id), "an older time is skipped");

                // A login stamps it too (Touch never waits for the disk: its file is written in the background).
                var sessions = new PlatformSessions(accounts, time);
                TestAssert.Equal(kept.Id, sessions.Login("rtmp-one", kept.Username, sessions.IssueTicket(kept), ClientVersion).AccountId, "the login");
                TestAssert.Equal(time.Now, accounts.LastSeen(kept.Id), "the login is stamped");
                TestAssert.False(File.ReadAllText(file).Contains("\"LastSeen\"", StringComparison.Ordinal), "Last seen is never written to accounts.json");
                accounts.Touch(999_999, time.Now);

                // History of the login to delete: a result, a report by it and one about it, and a reward code it redeemed.
                var seat = gone.Loadout with { PlayerId = (int)gone.SummonerId };
                resultGame = Guid.NewGuid();
                TestAssert.True(accounts.RecordResult(new PlatformResult(accounts.AllocateGameId(), resultGame, [gone.Id],
                    JsonSerializer.SerializeToElement(Completed(seat, gone)), time.Now)), "the result is recorded");
                accounts.RecordReport(gone.Id, kept.SummonerId, "SPAMMING", 1, "by the login that goes");
                accounts.RecordReport(kept.Id, gone.SummonerId, "VERBAL_ABUSE", 1, "about the login that goes");
                var code = accounts.CreateRewardCode(0, 100, Guid.NewGuid());
                accounts.RedeemRewardCode(gone.Id, code.Code);

                // A match handed to the match server without a result holds the login while it may run; an ended match does not.
                var handoff = Guid.NewGuid();
                accounts.PrepareMatch(new PlatformPendingMatch(accounts.AllocateGameId(), handoff, 0, [gone.Id], [seat]));
                TestAssert.Equal(DeleteOutcome.InGame, accounts.Delete(gone.Id), "a pending match counts as running when nobody says otherwise");
                TestAssert.Equal(DeleteOutcome.InGame, accounts.Delete(gone.Id, match => match == handoff), "a pending match that still runs");
                TestAssert.Equal(DeleteOutcome.NotFound, accounts.Delete(999_999), "an id that never was");
                TestAssert.Equal(DeleteOutcome.Deleted, accounts.Delete(gone.Id, _ => false), "an aborted match's handoff no longer holds the login");
                TestAssert.Equal(DeleteOutcome.NotFound, accounts.Delete(gone.Id, _ => false), "a second delete finds nothing");

                TestAssert.True(accounts.Find(gone.Id) is null && accounts.FindSummoner(gone.SummonerId) is null && accounts.FindName("Gone One") is null &&
                    accounts.Authenticate("gone1", Password) is null, "the login is gone");
                TestAssert.Equal(new DeletedAccount(gone.Id, "gone1", "Gone One", time.Now), accounts.FindDeleted(gone.Id), "its names are kept");
                TestAssert.Equal(gone.Id, accounts.FindDeletedSummoner(gone.SummonerId)?.Id, "found by its summoner id too");
                TestAssert.True(accounts.FindDeleted(kept.Id) is null && accounts.FindDeletedSummoner(kept.SummonerId) is null, "a live login is not a deleted one");
                var again = accounts.RegisterPublic("Gone1", Password, "Gone One");
                TestAssert.True(again.Id != gone.Id && again.SummonerId != gone.SummonerId, "the names are free again, under a new id");
            }

            // Read again: the store still loads (the code's redeemer is a deleted login) and keeps the history under the old names.
            using (var reread = new PlatformAccounts(folder, timeProvider: time))
            {
                TestAssert.True(File.ReadAllText(file).Contains("\"Deleted\"", StringComparison.Ordinal), "the deleted login is on record");
                TestAssert.Equal("gone1", reread.FindDeleted(goneId)?.Username, "the deleted names survive a restart");
                var result = reread.ResultsFor(goneId).Single();
                TestAssert.Equal(resultGame, result.HostGameId, "the result stays");
                TestAssert.Equal("Gone One", result.Result.GetProperty("Players")[0].GetProperty("Name").GetString(), "it still names the old summoner");
                TestAssert.Equal(2, reread.ListReports().Count(report => report.ReporterAccountId == goneId || report.ReportedSummonerId == goneSummonerId),
                    "both reports stay");
                TestAssert.Equal(goneId, reread.ListRewardCodes().Single().RedeemedBy, "the reward code still names who redeemed it");
                TestAssert.True(reread.Find(keptId) is not null && reread.Find(hostId) is not null, "the other logins stay");
            }

            // The social store: every row of the deleted login, both ways, goes; the others stay.
            var social = Path.Combine(root, "social");
            using (var store = new PlatformSocialStore(social))
            {
                store.SetContact(keptId, goneId, "General", "both", false);
                store.SetContact(goneId, keptId, "General", "both", false);
                store.SetContact(hostId, keptId, "General", "both", false);
                store.SetBlocked(goneId, hostId, true);
                store.SetBlocked(hostId, goneId, true);
                store.SetBlocked(keptId, hostId, true);
                store.RemoveAccount(goneId);
            }
            using (var store = new PlatformSocialStore(social))
            {
                TestAssert.True(store.Contacts(hostId).Single().ContactId == keptId && store.Contacts(keptId).Length == 0 &&
                    store.Contacts(goneId).Length == 0, "the contacts both ways are gone, the unrelated one stays");
                TestAssert.True(!store.IsBlocked(goneId, hostId) && !store.IsBlocked(hostId, goneId) && store.IsBlocked(keptId, hostId), "the blocks both ways are gone");
            }
        }
        finally { Remove(root); }
    }

    /// <summary>
    /// Final fix 6: Last seen lives in last-seen.json beside the store, so its frequent writes never change accounts.json (which an older release
    /// must still read). A store an earlier build wrote, with LastSeen inside accounts.json, moves it over at once. Writes keep the hourly rule
    /// (a later time in the same hour stays in memory only), and a damaged file reads as never seen, never a store that cannot open.
    /// Final fix 7: a logout, and every other end of a login, stamps it too.
    /// </summary>
    [Check("server.host-users-last-seen-file")]
    public static void LastSeenLivesInItsOwnFile()
    {
        var root = Directory.CreateTempSubdirectory("league-host-users-seen").FullName;
        try
        {
            var time = new Clock { Now = new DateTimeOffset(2026, 11, 11, 9, 5, 0, TimeSpan.Zero) };
            var folder = Path.Combine(root, "accounts");
            var store = Path.Combine(folder, "accounts.json");
            var seenFile = Path.Combine(folder, "last-seen.json");
            long oldId, newId;
            using (var accounts = new PlatformAccounts(folder, timeProvider: time))
            {
                oldId = accounts.RegisterPublic("old1", Password, "Old One").Id;
                newId = accounts.RegisterPublic("new1", Password, "New One").Id;
            }
            // The store an earlier build of batch 748 wrote: old1's LastSeen inside accounts.json.
            var earlier = new DateTimeOffset(2026, 11, 10, 8, 0, 0, TimeSpan.Zero);
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store))!;
            node["Accounts"]![0]!["LastSeen"] = earlier.ToString("O");
            File.WriteAllText(store, node.ToJsonString());
            using (var accounts = new PlatformAccounts(folder, timeProvider: time))
            {
                TestAssert.Equal(earlier, accounts.LastSeen(oldId), "the earlier store's time moved over");
                TestAssert.False(File.ReadAllText(store).Contains("\"LastSeen\"", StringComparison.Ordinal), "accounts.json sheds the field at once");
                TestAssert.True(File.ReadAllText(seenFile).Contains($"\"{oldId}\"", StringComparison.Ordinal), "last-seen.json holds it");
                TestAssert.True(accounts.LastSeen(newId) is null, "new1 was never seen");
                accounts.Touch(newId, time.Now);
                accounts.Touch(newId, time.Now.AddMinutes(50));
                TestAssert.Equal(time.Now.AddMinutes(50), accounts.LastSeen(newId), "the page has the latest time at once");
                accounts.Touch(newId, time.Now.AddMinutes(10));
                TestAssert.Equal(time.Now.AddMinutes(50), accounts.LastSeen(newId), "an older time never replaces a newer one");
            }
            using (var accounts = new PlatformAccounts(folder, timeProvider: time))
            {
                TestAssert.Equal(time.Now, accounts.LastSeen(newId), "the file has the hour's first time: later ones in that hour wrote nothing");
                accounts.Touch(newId, time.Now.AddMinutes(56));
            }
            using (var accounts = new PlatformAccounts(folder, timeProvider: time))
                TestAssert.Equal(time.Now.AddMinutes(56), accounts.LastSeen(newId), "the next hour's time was written");
            TestAssert.False(File.ReadAllText(store).Contains("\"LastSeen\"", StringComparison.Ordinal), "Last seen never writes accounts.json");

            // A damaged file is no Last seen yet; the next time written makes it whole again.
            File.WriteAllText(seenFile, "{ not json");
            using (var accounts = new PlatformAccounts(folder, timeProvider: time))
            {
                TestAssert.True(accounts.LastSeen(newId) is null && accounts.LastSeen(oldId) is null, "a damaged file reads as never seen");
                accounts.Touch(newId, time.Now.AddHours(3));
            }
            using (var accounts = new PlatformAccounts(folder, timeProvider: time))
            {
                TestAssert.Equal(time.Now.AddHours(3), accounts.LastSeen(newId), "the file is whole again");
                // A deleted login's time goes with it.
                TestAssert.Equal(DeleteOutcome.Deleted, accounts.Delete(newId), "new1 is deleted");
                TestAssert.True(accounts.LastSeen(newId) is null, "the deleted login has no Last seen");
            }

            // Final fix 7: a logout stamps it too, and so does every other end of a login, with the time the login was last there: a dropped
            // login that never comes back has when its connection closed, never when its resume window ran out.
            using (var accounts = new PlatformAccounts(folder, timeProvider: time))
            {
                var sessions = new PlatformSessions(accounts, time);
                var old = accounts.Find(oldId)!;
                time.Now = time.Now.AddHours(5);
                sessions.Login("rtmp-out", old.Username, sessions.IssueTicket(old), ClientVersion);
                time.Now = time.Now.AddMinutes(20);
                sessions.Logout("rtmp-out");
                TestAssert.Equal(time.Now, accounts.LastSeen(oldId), "a logout stamps its time");
                var dropped = sessions.Login("rtmp-drop", old.Username, sessions.IssueTicket(old), ClientVersion);
                TestAssert.True(sessions.AuthenticateChannel("rtmp-drop", old.Username, dropped.Token), "the channel authenticates");
                time.Now = time.Now.AddMinutes(10);
                var closed = time.Now;
                sessions.Suspend("rtmp-drop");
                time.Now = time.Now.AddMinutes(61);
                TestAssert.Equal(0, sessions.Online().Count, "the parked login ran out");
                TestAssert.Equal(closed, accounts.LastSeen(oldId), "it was last there when its connection closed");
            }
        }
        finally { Remove(root); }
    }

    /// <summary>
    /// Delete against the games (the match server runs): a lobby holds its member until they leave it; a launched match holds its player; a
    /// match whose lobby ended on a fault holds its player while the match server still runs it (its handoff has no result), and lets go once the
    /// match is stopped; a finished match on its results screen holds nobody, and deleting its owner then never makes it "end early".
    /// </summary>
    [Check("server.host-users-delete-match-states", TimeoutSeconds = 300)]
    public static void DeleteFollowsTheLobbyTheMatchServerAndTheResultsScreen() => MatchStatesAsync().GetAwaiter().GetResult();

    private static async Task MatchStatesAsync()
    {
        var root = Directory.CreateTempSubdirectory("league-host-users-matches").FullName;
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
            probe.Close();
            await using var manager = new GameManager(new HostSettings(GameData.Default.Paths.Root, ServerExecutable(),
                FirstGamePort: port, MaxGames: 1, BindAddress: "127.0.0.1"), Path.Combine(root, "games"));
            var feed = new HostActivity();
            var clock = new Clock();
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock, feed) { Push = (_, _) => { } };
            string Lines() => string.Join(" | ", feed.Recent().Select(entry => entry.Text));

            // A lobby in team selection holds its member, with no match time yet; once they leave it, the login can go.
            var waiting = accounts.RegisterPublic("lobby1", Password, "Lobby One");
            var lobby = Convert.ToInt32(((AsObject)games.Create(waiting, "Waiting", "", 1, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE"))["id"]);
            TestAssert.Equal(DeleteOutcome.InGame, games.DeleteAccount(waiting.Id), "a lobby member is in a lobby or a game");
            TestAssert.True(games.Playing().TryGetValue(waiting.Id, out var lobbyTime) && lobbyTime is null, "a lobby has no match time");
            games.Quit(waiting.Id);
            TestAssert.False(games.Playing().ContainsKey(waiting.Id), "the lobby let go: " + lobby);
            TestAssert.Equal(DeleteOutcome.Deleted, games.DeleteAccount(waiting.Id), "out of the lobby, the login goes");

            // A launched match holds its player. A fault in its lobby's timer (a result that does not match the roster) ends the lobby, while the
            // match server plays on: its handoff still holds the player until the match is stopped.
            var runner = accounts.RegisterPublic("runner1", Password, "Runner One");
            Solo(games, clock, runner, "Runs");
            var running = manager.List(PlatformGames.Guest(runner)).Single();
            TestAssert.Equal(DeleteOutcome.InGame, games.DeleteAccount(runner.Id), "a launched match holds its player");
            TestAssert.True(games.Playing().TryGetValue(runner.Id, out var launched) && launched is not null, "a launched match has a start time");
            var seat = MatchRoster.Read(Path.Combine(root, "games", running.Id.ToString("N"), "roster.json")).Single();
            var good = Completed(seat, runner);
            WriteResult(root, running.Id, good with { Players = [.. good.Players, good.Players[0] with { PlayerId = 77, IsBot = true }] });
            games.Tick();
            TestAssert.True(Lines().StartsWith("Game ended early", StringComparison.Ordinal), "the fault ended the lobby: " + Lines());
            TestAssert.False(games.Playing().ContainsKey(runner.Id), "no lobby holds the player now");
            TestAssert.True(manager.List(PlatformGames.Guest(runner)).Single(game => game.Id == running.Id).State is "Starting" or "Waiting for players",
                "the match server still runs the match");
            TestAssert.Equal(DeleteOutcome.InGame, games.DeleteAccount(runner.Id), "a running match's handoff holds its player");
            TestAssert.Equal(DeleteOutcome.InGame, accounts.Delete(runner.Id), "the store alone counts the match as running");
            await manager.StopAsync(PlatformGames.Guest(runner), running.Id);
            TestAssert.Equal(DeleteOutcome.Deleted, games.DeleteAccount(runner.Id), "a stopped match without a result no longer holds anyone");

            // A finished match: its result is read and its player is on the results screen, so the owner can go, and the next passes of the
            // timer neither fault nor say the game ended early.
            var winner = accounts.RegisterPublic("winner1", Password, "Winner One");
            var won = Solo(games, clock, winner, "Won");
            var finished = manager.List(PlatformGames.Guest(winner)).Single(game => game.Id != running.Id);
            WriteResult(root, finished.Id, Completed(MatchRoster.Read(Path.Combine(root, "games", finished.Id.ToString("N"), "roster.json")).Single(), winner));
            games.Tick();
            var ended = Lines();
            TestAssert.True(ended.StartsWith("Game ended · 5v5 map · Blue won", StringComparison.Ordinal), "the result is read: " + ended);
            TestAssert.False(games.Playing().ContainsKey(winner.Id), "a finished match holds nobody");
            TestAssert.Equal(DeleteOutcome.Deleted, games.DeleteAccount(winner.Id), "the owner of a finished match can go");
            games.Tick();
            games.Tick();
            TestAssert.Equal(ended, Lines(), "a deleted owner never makes a finished game end early");
            // No client ever loaded, so the match never reported Playing: the lobby is still START_REQUESTED, with its result read.
            TestAssert.True(games.Latest(winner.Id, won) is AsObject dto && (string)dto["gameState"] is not ("TERMINATED" or "FAILED_TO_START" or "TERMINATED_IN_ERROR"),
                "the finished game was not aborted: it waits on its results screen");
            TestAssert.Equal("Winner One", accounts.ResultsFor(winner.Id).Single().Result.GetProperty("Players")[0].GetProperty("Name").GetString(),
                "the deleted owner's result stays, under the old name");
            await manager.StopAsync(PlatformGames.Guest(winner), finished.Id);
        }
        finally { Remove(root); }
    }

    /// <summary>
    /// The page: its title with the count (the host's own login left out), the search box (shown by the script), the three columns, each user's
    /// marks (Signed up, Disabled), status (In lobby, In game, Online, Last seen, Never seen here) and row actions (New password, Disable or Enable, Delete),
    /// each action named for its user; the actions above the table; the script's filter; the page passes the host pages' bar.
    /// </summary>
    [Check("server.host-users-page", TimeoutSeconds = 120)]
    public static void TheUsersPageListsEveryUserWithMarksStatusAndActions() => UsersPageAsync().GetAwaiter().GetResult();

    private static async Task UsersPageAsync()
    {
        await using var site = await Site.OpenAsync("league-host-users-page");
        var accounts = site.Accounts;
        var self = accounts.RegisterPublic("self1", Password, "Self One", signedUp: true);
        var gone = accounts.RegisterPublic("gone1", Password, "Gone One");
        accounts.RemoveAccess(gone.Id);
        site.LogIn(accounts.RegisterPublic("online1", Password, "Online One"));
        var player = accounts.RegisterPublic("player1", Password, "Player One");
        site.Games!.Create(player, "Waiting", "", 1, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE");
        accounts.Touch(accounts.RegisterPublic("seen1", Password, "Seen One").Id, DateTimeOffset.UtcNow.AddHours(-3).AddMinutes(-5));
        accounts.RegisterPublic("never1", Password, "never1");

        var page = await site.PageAsync(Users, HttpStatusCode.OK);
        TestAssert.True(page.Contains("<h1>Users (6)</h1>", StringComparison.Ordinal) && !page.Contains("host_one", StringComparison.Ordinal),
            "the count and the list leave out the host's own login: " + page);
        TestAssert.True(page.Contains("<p class=table-search data-needs-script hidden><label for=user-search>Search users</label>" +
            "<input type=search id=user-search autocomplete=off></p>", StringComparison.Ordinal), "the search box, which the script shows: " + page);
        TestAssert.True(page.Contains("<table><thead><tr><th scope=col>User</th><th scope=col>Status</th><th scope=col>Actions</th></tr></thead><tbody id=users>",
            StringComparison.Ordinal) && page.Contains("<p id=users-none class=empty hidden>No users match your search.</p>", StringComparison.Ordinal),
            "the three columns and the search's empty line: " + page);
        TestAssert.True(page.Contains($"<a class=button href={HostInvite.AddPath}>Add a user</a>", StringComparison.Ordinal) &&
            !page.Contains("Get the Play file", StringComparison.Ordinal) && !page.Contains("Copy sign-up link", StringComparison.Ordinal),
            "Add a user; no Play file while Amberline's template is missing (it would download an error page); no sign-up link while sign-ups are off: " + page);
        // Review fix 3b: with the template beside the Platform (and the address known), Get the Play file is offered.
        Directory.CreateDirectory(site.Loop.Paths.App);
        File.WriteAllText(Path.Combine(site.Loop.Paths.App, HostInvite.TemplateFile), "@echo off\r\n");
        page = await site.PageAsync(Users, HttpStatusCode.OK);
        TestAssert.True(page.Contains($"<a class=\"button secondary\" href={HostInvite.PlayFilePath} download>Get the Play file</a>", StringComparison.Ordinal),
            "Get the Play file, once its template is there: " + page);
        TestAssert.Equal("gone1 never1 online1 player1 seen1 self1", string.Join(" ", Regex.Matches(page, "<tr><td><b>([a-z0-9_]+)</b>").Select(match => match.Groups[1].Value)),
            "every user, by username");
        TestAssert.True(page.Contains("aria-current=page>Users</a>", StringComparison.Ordinal), "the sidebar marks Users: " + page);

        var signedUp = Row(page, "self1");
        TestAssert.True(signedUp.Contains("<b>self1</b> <span class=tip>Self One</span> <span class=mark>Signed up</span></td><td class=words>Never seen here</td>", StringComparison.Ordinal) &&
            signedUp.Contains("New password<span class=vh> for self1</span>", StringComparison.Ordinal) && signedUp.Contains("Disable<span class=vh> self1</span>", StringComparison.Ordinal) &&
            signedUp.Contains($"<a class=\"button secondary small\" href=\"{Users}/delete?id={self.Id}\">Delete<span class=vh> self1</span></a>", StringComparison.Ordinal),
            "a signed-up user: the mark, the summoner name, never seen, and the three actions: " + signedUp);
        var disabled = Row(page, "gone1");
        TestAssert.True(disabled.Contains("<span class=mark>Disabled</span>", StringComparison.Ordinal) && disabled.Contains("Enable<span class=vh> gone1</span>", StringComparison.Ordinal) &&
            disabled.Contains("Delete<span class=vh> gone1</span>", StringComparison.Ordinal) && !disabled.Contains("New password", StringComparison.Ordinal) &&
            !disabled.Contains(">Disable<", StringComparison.Ordinal), "a disabled user: Enable (a new password) and Delete: " + disabled);
        TestAssert.True(Row(page, "online1").Contains("<td class=\"words ok\">Online</td>", StringComparison.Ordinal), "a logged-in user is online: " + page);
        TestAssert.True(Row(page, "player1").Contains("<td class=\"words ok\">In lobby</td>", StringComparison.Ordinal), "a lobby's member waits in its lobby, with no match time: " + page);
        // Only a launched match shows its time. The seam moves the lobby to IN_PROGRESS as the match server's report would; no process starts.
        var rooms = site.Games!;
        var launchOwner = site.Accounts.RegisterPublic("ingame1", Password, "In Game One");
        var launchGuest = site.Accounts.RegisterPublic("ingame2", Password, "In Game Two");
        var launched = Convert.ToInt32(((AsObject)rooms.Create(launchOwner, "Trees", "", 4, 6, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE"))["id"]);
        var guestJoin = (AsObject)rooms.Join(launchGuest, launched, "");
        rooms.Start(launchOwner.Id, launched, Convert.ToInt64(guestJoin["optimisticLock"]));
        rooms.StartPlayingForTest(launched);
        var launchedPage = await site.PageAsync(Users, HttpStatusCode.OK);
        TestAssert.True(Row(launchedPage, "ingame1").Contains("<td class=\"words ok\">In game · ", StringComparison.Ordinal) &&
            Row(launchedPage, "ingame2").Contains("<td class=\"words ok\">In game · ", StringComparison.Ordinal),
            "a launched match shows its time (In game · mm:ss) for every member: " + launchedPage);
        TestAssert.True(Row(page, "seen1").Contains("<td class=words>Last seen 3 hours ago</td>", StringComparison.Ordinal), "the last login, as relative time: " + page);
        TestAssert.True(Row(page, "never1").Contains("<b>never1</b></td><td class=words>Never seen here</td>", StringComparison.Ordinal),
            "a summoner name like the username is not repeated: " + page);
        TestAssert.True(page.Contains("<p class=tip>New password and Disable log the user out at once and keep their games. Delete cannot be undone.</p>",
            StringComparison.Ordinal), "the tip: " + page);
        HostPagesChecks.Accessible(page, "GET " + Users, HostDashboard.Policy);

        // The script: what needs it appears only with it, and the search hides the rows whose user and status cells do not hold what is typed.
        var script = await site.Browser.GetStringAsync(HostPages.ScriptPath);
        TestAssert.True(script.Contains("for (const element of document.querySelectorAll('[data-needs-script]')) element.hidden = false;", StringComparison.Ordinal) &&
            script.Contains("const search = byId('user-search');", StringComparison.Ordinal) &&
            script.Contains("row.hidden = words !== '' && !`${row.cells[0].textContent} ${row.cells[1].textContent}`.toLowerCase().includes(words);", StringComparison.Ordinal) &&
            script.Contains("show('users-none', shown === 0);", StringComparison.Ordinal) && !script.Contains("innerHTML", StringComparison.Ordinal),
            "page.js filters the table by text, without markup: " + script);
    }

    /// <summary>
    /// Disable is Remove access under its new name: the password stops working, the remote token and every session end, the cheat tick goes, the
    /// account stays. Enable makes a new password, shown once, and the login works again. Both write the feed; repeating either, or New password
    /// on a user disabled meanwhile, says so instead of acting.
    /// </summary>
    [Check("server.host-users-disable-enable", TimeoutSeconds = 120)]
    public static void DisableEndsEveryWayInAndEnableGivesANewPassword() => DisableEnableAsync().GetAwaiter().GetResult();

    private static async Task DisableEnableAsync()
    {
        await using var site = await Site.OpenAsync("league-host-users-disable");
        var accounts = site.Accounts;
        var user = accounts.RegisterPublic("user1", Password, "User One");
        var other = accounts.RegisterPublic("user2", Password, "User Two");
        site.Cheats(site.Host.Id, user.Id, other.Id);
        var tunnel = site.Access.Issue(user.Id).Token;
        var otherTunnel = site.Access.Issue(other.Id).Token;
        site.LogIn(user);
        site.LogIn(other);
        var id = ("accountId", user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var token = Token(await site.PageAsync(Users, HttpStatusCode.OK));

        // Final fix 2: the remote sign-in file cannot be written just now (held open). Disable still ends every way in and says which change
        // it could not save, as Delete does; the tunnel refuses the disabled login's token in any case (AirRemoteAccess.Allowed).
        string disabled;
        using (new FileStream(Path.Combine(site.Root, "data", "remote", "remote-access.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            disabled = await PostAsync(site.Browser, Users + "/disable", token, HttpStatusCode.OK, id);
        TestAssert.True(disabled.Contains("<p class=notice role=status>user1 can no longer log in, and was logged out. Their games stay.</p>", StringComparison.Ordinal) &&
            Row(disabled, "user1").Contains("<span class=mark>Disabled</span>", StringComparison.Ordinal), "the notice and the row: " + disabled);
        TestAssert.True(disabled.Contains("<p class=warn>Amberline could not save every change just now: their remote sign-in.</p>", StringComparison.Ordinal),
            "the page says the remote sign-in could not be saved: " + disabled);
        TestAssert.True(accounts.Authenticate("user1", Password) is null, "the password no longer works");
        TestAssert.True(site.Access.AccountFor(tunnel) is null && site.Sessions.AccountForConnection("rtmp-" + user.Id) is null, "the token and the session ended");
        TestAssert.True(accounts.Find(user.Id) is not null, "no account is deleted");
        TestAssert.Equal($"{site.Host.Id},{other.Id}", site.CheatLogins(), "Disable clears only user1's cheat tick");
        TestAssert.Equal("user1 disabled | user1 logged out", Lines(site, 2), "the feed names the logout and the disable");
        TestAssert.True(site.Access.AccountFor(otherTunnel) == other.Id && accounts.Authenticate("user2", Password) is not null &&
            site.Sessions.AccountForConnection("rtmp-" + other.Id) == other.Id, "user2 keeps every way in");

        // A second tab: Disable again, and New password, on a user that is disabled now.
        var again = await PostAsync(site.Browser, Users + "/disable", token, HttpStatusCode.OK, id);
        TestAssert.True(again.Contains("<p class=notice role=status>user1 is already disabled.</p>", StringComparison.Ordinal), "already disabled: " + again);
        var stale = await PostAsync(site.Browser, Users + "/password", token, HttpStatusCode.Conflict, id);
        TestAssert.True(stale.Contains("user1 is disabled. Enable gives them a new password.", StringComparison.Ordinal) && accounts.Find(user.Id)!.AccessRemovedAt is not null,
            "New password does not let a disabled user in: " + stale);
        TestAssert.Equal("user1 disabled | user1 logged out", Lines(site, 2), "neither wrote the feed");

        var enabled = await PostAsync(site.Browser, Users + "/enable", token, HttpStatusCode.OK, id);
        var password = Regex.Match(WebUtility.HtmlDecode(enabled), @"password: (\S+)").Groups[1].Value;
        TestAssert.True(enabled.Contains("<h1>user1 can log in again</h1>", StringComparison.Ordinal) && password.Length >= 10 &&
            accounts.Authenticate("user1", password) is not null, "Enable shows a new password once, and it works: " + enabled);
        TestAssert.Equal("user1 enabled", Lines(site, 1), "the feed names the enable");
        var listed = await site.PageAsync(Users, HttpStatusCode.OK);
        TestAssert.True(!listed.Contains(password, StringComparison.Ordinal) && Row(listed, "user1").Contains("Disable<span class=vh> user1</span>", StringComparison.Ordinal) &&
            !Row(listed, "user1").Contains("<span class=mark>Disabled</span>", StringComparison.Ordinal), "shown only once; user1 is active again: " + listed);
        var already = await PostAsync(site.Browser, Users + "/enable", token, HttpStatusCode.OK, id);
        TestAssert.True(already.Contains("<p class=notice role=status>user1 can already log in.</p>", StringComparison.Ordinal) &&
            accounts.Authenticate("user1", password) is not null, "Enable again changes nothing: " + already);
    }

    /// <summary>Delete asks first, on a page that names the user and says it cannot be undone; then the login goes and its names are free.</summary>
    [Check("server.host-users-delete-frees-the-name", TimeoutSeconds = 120)]
    public static void DeleteAsksFirstThenFreesTheNames() => DeleteFreesAsync().GetAwaiter().GetResult();

    private static async Task DeleteFreesAsync()
    {
        await using var site = await Site.OpenAsync("league-host-users-delete");
        var accounts = site.Accounts;
        var user = accounts.RegisterPublic("user1", Password, "User One");
        site.LogIn(user);
        var confirm = await site.PageAsync($"{Users}/delete?id={user.Id}", HttpStatusCode.OK);
        TestAssert.True(confirm.Contains("<h1>Delete user1?</h1>", StringComparison.Ordinal) && confirm.Contains("This cannot be undone.", StringComparison.Ordinal) &&
            confirm.Contains("User One", StringComparison.Ordinal) && confirm.Contains($"<form method=post action={Users}/delete>", StringComparison.Ordinal) &&
            confirm.Contains($"<input type=hidden name=accountId value={user.Id}><div class=buttons><button type=submit>Delete user</button> " +
                $"<a class=\"button secondary\" href={Users}>Cancel</a></div></form>", StringComparison.Ordinal), "the confirm page: " + confirm);
        HostPagesChecks.Accessible(confirm, "GET the delete confirm", HostDashboard.Policy);
        TestAssert.True(accounts.Find(user.Id) is not null, "the confirm page deletes nothing");

        // Fix round 1: each tidy-up step stands on its own. The remote sign-in file cannot be written just now (held open), so that step fails
        // after its retries; the session still ends, the cheat tick still goes, the feed names the delete, and the page says what was not saved.
        var tunnel = site.Access.Issue(user.Id).Token;
        site.Cheats(site.Host.Id, user.Id);
        string done;
        using (new FileStream(Path.Combine(site.Root, "data", "remote", "remote-access.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            done = await PostAsync(site.Browser, Users + "/delete", Token(confirm), HttpStatusCode.OK, ("accountId", user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        TestAssert.True(done.Contains("<p class=notice role=status>user1 is deleted.</p>", StringComparison.Ordinal) && done.Contains("<h1>Users (0)</h1>", StringComparison.Ordinal) &&
            Row(done, "user1").Length == 0, "the notice, and the row is gone: " + done);
        TestAssert.True(done.Contains("<p class=warn>Amberline could not save every change just now: their remote sign-in.</p>", StringComparison.Ordinal),
            "the page says which step could not be saved: " + done);
        TestAssert.True(accounts.Find(user.Id) is null && site.Sessions.AccountForConnection("rtmp-" + user.Id) is null, "the login and its session are gone");
        TestAssert.True(site.Access.AccountFor(tunnel) is null, "the remote sign-in ends in this run all the same");
        TestAssert.Equal($"{site.Host.Id}", site.CheatLogins(), "the cheat tick goes although an earlier step failed");
        TestAssert.Equal("user1 deleted | user1 logged out", Lines(site, 2), "the feed names the logout and the delete");
        var taken = accounts.RegisterPublic("user1", Password, "User One");
        TestAssert.True(taken.Id != user.Id, "the same username and summoner name make a new login");
        TestAssert.True(Row(await site.PageAsync(Users, HttpStatusCode.OK), "user1").Contains($"value={taken.Id}>", StringComparison.Ordinal), "the new login is listed");
    }

    /// <summary>
    /// Delete in the full Platform (its own games, chat and remote access): the login goes with its live state (session, remote token, social rows
    /// both ways, cheat tick), and its history stays under its old names: the result, the reports and the reward code it redeemed, also on Advanced.
    /// </summary>
    [Check("server.host-users-delete-keeps-history", TimeoutSeconds = 180)]
    public static void DeleteKeepsHistoryAndDropsLiveState() => KeepsHistoryAsync().GetAwaiter().GetResult();

    private static async Task KeepsHistoryAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        // A new login takes its first champion from the champion list: read it the suite's way first (see HostInviteChecks).
        var champions = ChampionCatalog.All.Count;
        var home = Home.Create("league-host-users-history");
        var data = home.Loop.Paths.Data;
        long hostId, victimId, otherId, victimSummonerId;
        using (var store = new PlatformAccounts(data))
        {
            var host = store.RegisterPublic("host_one", Password, "Host One");
            var victim = store.RegisterPublic("victim1", Password, "Victim One");
            var other = store.RegisterPublic("other1", Password, "Other One");
            (hostId, victimId, otherId, victimSummonerId) = (host.Id, victim.Id, other.Id, victim.SummonerId);
            TestAssert.True(store.RecordResult(new PlatformResult(store.AllocateGameId(), Guid.NewGuid(), [victim.Id],
                JsonSerializer.SerializeToElement(Completed(victim.Loadout with { PlayerId = (int)victim.SummonerId }, victim)), DateTimeOffset.UtcNow)), "a result");
            store.RecordReport(victim.Id, other.SummonerId, "SPAMMING", 1, "by victim1");
            store.RecordReport(other.Id, victim.SummonerId, "VERBAL_ABUSE", 1, "about victim1");
            store.RedeemRewardCode(victim.Id, store.CreateRewardCode(0, 100, Guid.NewGuid()).Code);
        }
        using (var social = new PlatformSocialStore(Path.Combine(data, "social")))
        {
            social.SetContact(victimId, otherId, "General", "both", false);
            social.SetContact(otherId, victimId, "General", "both", false);
            social.SetContact(otherId, hostId, "General", "both", false);
            social.SetBlocked(victimId, hostId, true);
            social.SetBlocked(hostId, victimId, true);
        }
        var remote = new AirRemoteAccess(Path.Combine(data, "remote"));
        var victimToken = remote.Issue(victimId).Token;
        var otherToken = remote.Issue(otherId).Token;
        WriteSettings(home, new { serverName = "Test Server", publicHost = TestHost, addressSource = HostAddress.Typed, hostAccount = hostId,
            cheatAccounts = new[] { hostId, victimId, otherId } });
        // Final fix 1: the log the support log quotes still names the login after it is deleted.
        Directory.CreateDirectory(home.Loop.Paths.Logs);
        File.WriteAllLines(Path.Combine(home.Loop.Paths.Logs, "amberline-2026-11-03.log"), ["victim1 played as Victim One"]);
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            var confirm = await browser.GetStringAsync($"{Users}/delete?id={victimId}");
            TestAssert.True(confirm.Contains("<h1>Delete victim1?</h1>", StringComparison.Ordinal), confirm);
            var done = await PostAsync(browser, Users + "/delete", Token(confirm), HttpStatusCode.OK, ("accountId", victimId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            TestAssert.True(done.Contains("<p class=notice role=status>victim1 is deleted.</p>", StringComparison.Ordinal), done);
            TestAssert.True(home.Loop.Activity.Recent().Any(entry => entry.Text == "victim1 deleted"), "the feed names the delete");
            // Advanced: the reports and the reward code still name the deleted login by the summoner name it had.
            var advanced = await browser.GetStringAsync("/manage");
            TestAssert.True(Regex.Count(advanced, "<td>Victim One</td>") == 3 && !advanced.Contains("a removed login", StringComparison.Ordinal) &&
                !advanced.Contains($"summoner {victimSummonerId}", StringComparison.Ordinal), "the reporter, the reported and the code's user keep the old name: " + advanced);
            // The support log hides a deleted login's names as it hides every login's (the host may paste it in public).
            var support = WebUtility.HtmlDecode(Regex.Match(await browser.GetStringAsync(HostSupport.PagePath), "<textarea[^>]*>(.*?)</textarea>",
                RegexOptions.Singleline).Groups[1].Value);
            TestAssert.True(support.Contains("<login> played as <login>", StringComparison.Ordinal) &&
                !support.Contains("victim1", StringComparison.OrdinalIgnoreCase) && !support.Contains("Victim One", StringComparison.OrdinalIgnoreCase),
                "the support log masks the deleted login: " + support);
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        using (var store = new PlatformAccounts(data))
        {
            TestAssert.True(store.Find(victimId) is null && store.FindDeleted(victimId) is { Username: "victim1", Summoner: "Victim One" }, "deleted, its names kept");
            TestAssert.Equal("Victim One", store.ResultsFor(victimId).Single().Result.GetProperty("Players")[0].GetProperty("Name").GetString(),
                "the recorded result still lists the old summoner name");
            TestAssert.Equal(2, store.ListReports().Length, "the reports stay");
            TestAssert.Equal(victimId, store.ListRewardCodes().Single().RedeemedBy, "the reward code still names who redeemed it");
            TestAssert.True(store.Find(otherId) is not null && store.Find(hostId) is not null, "the other logins stay");
        }
        using (var social = new PlatformSocialStore(Path.Combine(data, "social")))
            TestAssert.True(social.Contacts(victimId).Length == 0 && social.Contacts(otherId).Single().ContactId == hostId &&
                !social.IsBlocked(victimId, hostId) && !social.IsBlocked(hostId, victimId), "the social rows of the deleted login are gone, both ways");
        var tokens = new AirRemoteAccess(Path.Combine(data, "remote"));
        TestAssert.True(tokens.AccountFor(victimToken) is null && tokens.AccountFor(otherToken) == otherId, "only the deleted login's remote token is gone");
        using (var json = JsonDocument.Parse(File.ReadAllText(home.Loop.Paths.Settings)))
            TestAssert.Equal($"{hostId},{otherId}", string.Join(",", json.RootElement.GetProperty("cheatAccounts").EnumerateArray().Select(value => value.GetInt64())),
                "the cheat tick of the deleted login is gone");
        TestAssert.Equal(champions, ChampionCatalog.All.Count, "the champion list stays as it was");
        HostModeChecks.Delete(home.Root);
    }

    /// <summary>
    /// Final fix 2: the tunnel trusts no token alone. A token that came back for a login the host deleted or disabled (its revoke was not saved,
    /// then the Platform restarted and read the old token file) is refused with 401 at the sign-in check the companion makes before any tunnel,
    /// and counts as a failed attempt; a live login's token still works.
    /// </summary>
    [Check("server.host-users-revoked-tokens-refused", TimeoutSeconds = 180)]
    public static void ADeletedOrDisabledLoginsTokenIsRefused() => RevokedTokensAsync().GetAwaiter().GetResult();

    private static async Task RevokedTokensAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var champions = ChampionCatalog.All.Count;
        var home = Home.Create("league-host-users-tokens");
        var data = home.Loop.Paths.Data;
        long hostId;
        string deletedToken, disabledToken, liveToken;
        using (var store = new PlatformAccounts(data))
        {
            hostId = store.RegisterPublic("host_one", Password, "Host One").Id;
            var gone = store.RegisterPublic("gone1", Password, "Gone One");
            var off = store.RegisterPublic("off1", Password, "Off One");
            var live = store.RegisterPublic("live1", Password, "Live One");
            TestAssert.Equal(DeleteOutcome.Deleted, store.Delete(gone.Id), "gone1 is deleted");
            store.RemoveAccess(off.Id);
            // The tokens a revoke that was never saved leaves in the file the next start reads.
            var remote = new AirRemoteAccess(Path.Combine(data, "remote"));
            (deletedToken, disabledToken, liveToken) = (remote.Issue(gone.Id).Token, remote.Issue(off.Id).Token, remote.Issue(live.Id).Token);
        }
        WriteSettings(home, new { serverName = "Test Server", publicHost = TestHost, addressSource = HostAddress.Typed, hostAccount = hostId });
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = HostSessionChecks.Browser(home);
            await WaitForHealthAsync(browser, "ready", run);
            async Task<HttpStatusCode> CheckAsync(string token)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{home.RemotePort}{AirRemoteTunnel.SignInPath}");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using var answer = await browser.SendAsync(request);
                return answer.StatusCode;
            }
            TestAssert.Equal(HttpStatusCode.Unauthorized, await CheckAsync(deletedToken), "a deleted login's token is refused");
            TestAssert.Equal(HttpStatusCode.Unauthorized, await CheckAsync(disabledToken), "a disabled login's token is refused");
            TestAssert.Equal(HttpStatusCode.OK, await CheckAsync(liveToken), "a live login's token works");
            // Each refusal is a failed attempt: ten in the window throttle the address, the live token too.
            for (var attempt = 3; attempt <= 10; attempt++)
                TestAssert.Equal(HttpStatusCode.Unauthorized, await CheckAsync(deletedToken), $"refusal {attempt}");
            TestAssert.Equal(HttpStatusCode.TooManyRequests, await CheckAsync(liveToken), "the refusals counted as failed attempts");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        TestAssert.Equal(champions, ChampionCatalog.All.Count, "the champion list stays as it was");
        HostModeChecks.Delete(home.Root);
    }

    /// <summary>The host's own login is never changed or deleted here; a user in a lobby or a game (a match handed to the match server included) is not deleted.</summary>
    [Check("server.host-users-delete-refusals", TimeoutSeconds = 120)]
    public static void DeleteRefusesTheHostsOwnLoginAndAUserInAGame() => RefusalsAsync().GetAwaiter().GetResult();

    private static async Task RefusalsAsync()
    {
        await using (var site = await Site.OpenAsync("league-host-users-refusals"))
        {
            var host = ("accountId", site.Host.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var player = site.Accounts.RegisterPublic("player1", Password, "Player One");
            var token = Token(await site.PageAsync(Users, HttpStatusCode.OK));
            var own = await site.PageAsync($"{Users}/delete?id={site.Host.Id}", HttpStatusCode.BadRequest);
            TestAssert.True(own.Contains("<h1>This is your own login</h1>", StringComparison.Ordinal), "the confirm page refuses the host's own login: " + own);
            HostPagesChecks.Accessible(own, "the own-login page", HostDashboard.Policy);
            foreach (var action in new[] { "/delete", "/disable", "/password", "/enable" })
                TestAssert.True((await PostAsync(site.Browser, Users + action, token, HttpStatusCode.BadRequest, host)).Contains("<h1>This is your own login</h1>",
                    StringComparison.Ordinal), action + " refuses the host's own login");
            TestAssert.True(site.Accounts.Authenticate("host_one", Password) is not null, "the host's login is unchanged");

            site.Games!.Create(player, "Waiting", "", 1, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE");
            var refused = await PostAsync(site.Browser, Users + "/delete", token, HttpStatusCode.Conflict,
                ("accountId", player.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            TestAssert.True(refused.Contains("<h1>player1 is in a lobby or a game</h1>", StringComparison.Ordinal), "a lobby's member is in a lobby or a game: " + refused);
            HostPagesChecks.Accessible(refused, "the in-a-game page", HostDashboard.Policy);
            TestAssert.True(site.Accounts.Find(player.Id) is not null && !site.Loop.Activity.Recent().Any(entry => entry.Text.EndsWith("deleted", StringComparison.Ordinal)),
                "nothing was deleted");
        }
        // Without the games (the store's own rule): a match handed to the match server without a result holds its player.
        await using (var site = await Site.OpenAsync("league-host-users-refusals-handoff", withGames: false))
        {
            var player = site.Accounts.RegisterPublic("handoff1", Password, "Handoff One");
            site.Accounts.PrepareMatch(new PlatformPendingMatch(site.Accounts.AllocateGameId(), Guid.NewGuid(), 0, [player.Id],
                [player.Loadout with { PlayerId = (int)player.SummonerId }]));
            var refused = await PostAsync(site.Browser, Users + "/delete", Token(await site.PageAsync(Users, HttpStatusCode.OK)), HttpStatusCode.Conflict,
                ("accountId", player.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            TestAssert.True(refused.Contains("<h1>handoff1 is in a lobby or a game</h1>", StringComparison.Ordinal) && site.Accounts.Find(player.Id) is not null,
                "a pending match handoff holds its player: " + refused);
        }
    }

    /// <summary>A stale page (another tab acted first): an action on a user that no longer exists is a plain 404 page, never a failure.</summary>
    [Check("server.host-users-stale-action", TimeoutSeconds = 120)]
    public static void AnActionOnAGoneUserSaysSo() => StaleAsync().GetAwaiter().GetResult();

    private static async Task StaleAsync()
    {
        await using var site = await Site.OpenAsync("league-host-users-stale");
        var gone = site.Accounts.RegisterPublic("gone1", Password, "Gone One");
        var page = await site.PageAsync(Users, HttpStatusCode.OK);
        var token = Token(page);
        TestAssert.Equal(DeleteOutcome.Deleted, site.Accounts.Delete(gone.Id), "another tab deleted gone1");
        foreach (var action in new[] { "/disable", "/enable", "/password", "/delete" })
        {
            var answer = await PostAsync(site.Browser, Users + action, token, HttpStatusCode.NotFound,
                ("accountId", gone.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            TestAssert.True(answer.Contains("<h1>This user no longer exists.</h1>", StringComparison.Ordinal), action + ": " + answer);
        }
        var confirm = await site.PageAsync($"{Users}/delete?id={gone.Id}", HttpStatusCode.NotFound);
        TestAssert.True(confirm.Contains("<h1>This user no longer exists.</h1>", StringComparison.Ordinal), "the confirm page of a gone user: " + confirm);
        HostPagesChecks.Accessible(confirm, "the gone page", HostDashboard.Policy);
        await PostAsync(site.Browser, Users + "/disable", token, HttpStatusCode.NotFound, ("accountId", "x"));
        await PostAsync(site.Browser, Users + "/disable", null, HttpStatusCode.BadRequest, ("accountId", "1"));
        TestAssert.Equal(0, site.Loop.Activity.Recent().Count, "nothing was done");
    }

    /// <summary>Names with markup characters and names longer than registration allows are escaped and wrap in the table and on the confirm page.</summary>
    [Check("server.host-users-escapes-names", TimeoutSeconds = 120)]
    public static void NamesAreEscapedAndWrap() => EscapesAsync().GetAwaiter().GetResult();

    private static async Task EscapesAsync()
    {
        // 32 characters, more than registration allows (27): a name an older store or another tool could hold.
        var longName = new string('W', 32);
        await using var site = await Site.OpenAsync("league-host-users-escapes", data =>
        {
            using (var store = new PlatformAccounts(data))
            {
                store.RegisterPublic("host_one", Password, "Host One");
                store.RegisterPublic("bad1", Password, "<b>Bad</b>");
                store.RegisterPublic("long1", Password, "Long One");
            }
            var file = Path.Combine(data, "accounts.json");
            File.WriteAllText(file, File.ReadAllText(file).Replace("\"Long One\"", $"\"{longName}\"", StringComparison.Ordinal));
        });
        var bad = site.Accounts.ListAccounts().Single(account => account.Username == "bad1");
        var page = await site.PageAsync(Users, HttpStatusCode.OK);
        TestAssert.True(Row(page, "bad1").Contains("<span class=tip>&lt;b&gt;Bad&lt;/b&gt;</span>", StringComparison.Ordinal) &&
            !page.Contains("<b>Bad</b>", StringComparison.Ordinal), "a summoner name with markup is escaped: " + page);
        TestAssert.True(Row(page, "long1").StartsWith($"<tr><td><b>long1</b> <span class=tip>{longName}</span></td><td class=words>", StringComparison.Ordinal),
            "a long name stands in the user cell, which wraps anywhere; the status cell keeps its words whole: " + page);
        var confirm = await site.PageAsync($"{Users}/delete?id={bad.Id}", HttpStatusCode.OK);
        TestAssert.True(confirm.Contains("&lt;b&gt;Bad&lt;/b&gt;", StringComparison.Ordinal) && !confirm.Contains("<b>Bad</b>", StringComparison.Ordinal),
            "the confirm page escapes it too: " + confirm);
        var css = await site.Browser.GetStringAsync(HostPages.StyleHref);
        TestAssert.True(css.Contains("th,td{padding:8px 12px 8px 0;border-bottom:1px solid rgba(111,90,49,.45);text-align:left;vertical-align:top;overflow-wrap:anywhere}",
            StringComparison.Ordinal) && css.Contains("td.words{overflow-wrap:normal}", StringComparison.Ordinal), "cells wrap anywhere; td.words keeps whole words");
    }

    /// <summary>No users but the host: a calm line instead of an empty table, and the way to add one.</summary>
    [Check("server.host-users-empty", TimeoutSeconds = 120)]
    public static void NoUsersShowsACalmLine() => EmptyAsync().GetAwaiter().GetResult();

    private static async Task EmptyAsync()
    {
        // Review fix 3b: Amberline does not know its address yet, so although its template is there, no Play file is offered.
        await using var site = await Site.OpenAsync("league-host-users-empty", address: () => null);
        Directory.CreateDirectory(site.Loop.Paths.App);
        File.WriteAllText(Path.Combine(site.Loop.Paths.App, HostInvite.TemplateFile), "@echo off\r\n");
        var page = await site.PageAsync(Users, HttpStatusCode.OK);
        TestAssert.True(page.Contains("<h1>Users (0)</h1>", StringComparison.Ordinal) &&
            page.Contains("<p class=empty>No users yet. Add one or turn on sign-ups.</p>", StringComparison.Ordinal) &&
            !page.Contains("<table", StringComparison.Ordinal) && !page.Contains("user-search", StringComparison.Ordinal) &&
            page.Contains($"href={HostInvite.AddPath}>Add a user</a>", StringComparison.Ordinal), "the empty line and Add a user: " + page);
        TestAssert.False(page.Contains("Get the Play file", StringComparison.Ordinal), "no Play file before the address is known: " + page);
        HostPagesChecks.Accessible(page, "the empty Users page", HostDashboard.Policy);
    }

    /// <summary>The old pages' addresses lead to the new ones (303), the old forms' addresses are gone, and the sidebar says Users.</summary>
    [Check("server.host-users-old-paths-redirect", TimeoutSeconds = 180)]
    public static void TheOldAddressesLeadToTheUsersPage() => OldPathsAsync().GetAwaiter().GetResult();

    private static async Task OldPathsAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var champions = ChampionCatalog.All.Count;
        var home = Home.Create("league-host-users-old-paths");
        long hostId, userId;
        using (var store = new PlatformAccounts(home.Loop.Paths.Data))
        {
            hostId = store.RegisterPublic("host_one", Password, "Host One").Id;
            userId = store.RegisterPublic("user1", Password, "User One").Id;
        }
        WriteSettings(home, new { serverName = "Test Server", publicHost = TestHost, addressSource = HostAddress.Typed, hostAccount = hostId });
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = home.Client();
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            var dashboard = await browser.GetStringAsync(HostDashboard.PagePath);
            TestAssert.True(dashboard.Contains($"<a href={Users}>Users</a>", StringComparison.Ordinal) &&
                dashboard.Contains($"href={HostInvite.AddPath}>Add a user</a>", StringComparison.Ordinal) &&
                !dashboard.Contains("href=/host/invite", StringComparison.Ordinal) && !dashboard.Contains("href=/host/friends", StringComparison.Ordinal),
                "the sidebar has Users and the dashboard adds a user on the new page: " + dashboard);
            foreach (var (old, now) in new[] { ("/host/invite", HostInvite.AddPath), ("/host/friends", Users) })
            {
                using var moved = await browser.GetAsync(old);
                TestAssert.Equal(HttpStatusCode.SeeOther, moved.StatusCode, old);
                TestAssert.Equal(now, moved.Headers.Location?.OriginalString, old + " leads to " + now);
            }
            var token = Token(await browser.GetStringAsync(Users));
            foreach (var old in new[] { "/host/friends/password", "/host/friends/remove" })
                await PostAsync(browser, old, token, HttpStatusCode.NotFound, ("accountId", userId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        using (var store = new PlatformAccounts(home.Loop.Paths.Data))
            TestAssert.True(store.Authenticate("user1", Password) is not null, "the old addresses changed nothing");
        TestAssert.Equal(champions, ChampionCatalog.All.Count, "the champion list stays as it was");
        HostModeChecks.Delete(home.Root);
    }

    /// <summary>A user's row on the Users page (empty when the user is not listed).</summary>
    private static string Row(string page, string username) =>
        Regex.Match(page, $"<tr><td><b>{Regex.Escape(username)}</b>.*?</tr>", RegexOptions.Singleline).Value;

    /// <summary>The newest <paramref name="count"/> lines of the site's feed, newest first.</summary>
    private static string Lines(Site site, int count) => string.Join(" | ", site.Loop.Activity.Recent(count).Select(entry => entry.Text));

    /// <summary>
    /// The Users page on a web app of its own, as the full Platform maps it (the gate, the shell, the pages) over the stores, sessions, games and
    /// chat this check holds, so a check can log a user in or seat them in a lobby. The games' match server is a stand-in: nothing launches.
    /// The browser is signed in. <c>host_one</c> is the host's own login.
    /// </summary>
    private sealed class Site : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required HostLoop Loop { get; init; }
        public required PlatformAccounts Accounts { get; init; }
        public required PlatformSessions Sessions { get; init; }
        public required AirRemoteAccess Access { get; init; }
        public required GameManager Manager { get; init; }
        public required PlatformGames? Games { get; init; }
        public required AirXmppHost Chat { get; init; }
        public required WebApplication App { get; init; }
        public required HttpClient Browser { get; init; }
        public required PlatformAccount Host { get; init; }

        /// <param name="prepare">Runs on the empty data folder before the stores open (a store written beforehand).</param>
        /// <param name="withGames">False maps the page without the games, so only the store's own rules decide.</param>
        /// <param name="address">The address Amberline knows (amberline.example.test by default; null: not known yet).</param>
        public static async Task<Site> OpenAsync(string prefix, Action<string>? prepare = null, bool withGames = true, Func<string?>? address = null)
        {
            var root = AirBootstrapChecks.TemporaryDirectory(prefix);
            var paths = HostPaths.Resolve(Path.Combine(root, "data"), Path.Combine(root, "Local"), Path.Combine(root, "app", "platform"));
            Directory.CreateDirectory(paths.Data);
            prepare?.Invoke(paths.Data);
            var accounts = new PlatformAccounts(paths.Data);
            var host = accounts.ListAccounts().FirstOrDefault(account => account.Username == "host_one") ?? accounts.RegisterPublic("host_one", Password, "Host One");
            File.WriteAllText(paths.Settings, JsonSerializer.Serialize(new { hostAccount = host.Id }));
            var loop = new HostLoop(paths, _ => { }, TextWriter.Null);
            var sessions = new PlatformSessions(accounts, activity: loop.Activity);
            var manager = new GameManager(new HostSettings(root, Environment.ProcessPath!, BindAddress: "127.0.0.1"), Path.Combine(root, "games"));
            var games = new PlatformGames(accounts, manager, "127.0.0.1", activity: loop.Activity) { Push = (_, _) => { } };
            var chat = new AirXmppHost(accounts, sessions, Path.Combine(paths.Data, "social"), 0);
            var access = new AirRemoteAccess(Path.Combine(paths.Data, "remote"));
            var port = AirManagementChecks.FreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
            builder.Services.AddAntiforgery(options => options.Cookie.Name = "Amberline.Forms");
            builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(paths.Keys));
            var app = builder.Build();
            HostGate.Use(app, port, loop.Sessions, HostDashboard.PagePath);
            HostPages.Use(app, loop, () => "Test Server", nav: true);
            HostInvite.Map(app, loop, address ?? (() => TestHost), () => "Test Server", accounts, sessions, access, 8391, new string('A', 64),
                games: withGames ? games : null, chat: chat);
            await app.StartAsync();
            var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer(), UseProxy = false })
                { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
            using (var open = await browser.GetAsync(loop.PageUrl(port))) TestAssert.Equal(HttpStatusCode.Redirect, open.StatusCode, "the launch code signs in");
            return new Site
            {
                Root = root, Loop = loop, Accounts = accounts, Sessions = sessions, Access = access, Manager = manager, Games = withGames ? games : null,
                Chat = chat, App = app, Browser = browser, Host = host,
            };
        }

        /// <summary>A live login on its own connection, <c>rtmp-&lt;id&gt;</c>, as the stock client's login and channel login make it.</summary>
        public void LogIn(PlatformAccount account)
        {
            var session = Sessions.Login("rtmp-" + account.Id, account.Username, Sessions.IssueTicket(account), ClientVersion);
            TestAssert.True(Sessions.AuthenticateChannel("rtmp-" + account.Id, account.Username, session.Token), account.Username + " logs in");
        }

        /// <summary>Ticks these logins in Settings' Cheat commands, as Settings saves the list.</summary>
        public void Cheats(params long[] accounts) => File.WriteAllText(Loop.Paths.Settings,
            JsonSerializer.Serialize(new { hostAccount = Host.Id, cheatAccounts = accounts }));

        /// <summary>The ticked logins as saved, joined with commas.</summary>
        public string CheatLogins()
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Loop.Paths.Settings));
            return string.Join(",", json.RootElement.GetProperty("cheatAccounts").EnumerateArray().Select(value => value.GetInt64()));
        }

        public async Task<string> PageAsync(string path, HttpStatusCode expected)
        {
            using var answer = await Browser.GetAsync(path);
            var page = await answer.Content.ReadAsStringAsync();
            TestAssert.Equal(expected, answer.StatusCode, $"GET {path}: {page}");
            return page;
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            Browser.Dispose();
            await Chat.DisposeAsync();
            await Manager.DisposeAsync();
            Accounts.Dispose();
            HostModeChecks.Delete(Root);
        }
    }

    /// <summary>
    /// Review fix 2: a match's result that is recovered after one of its players was deleted (a timer fault after the match server wrote it, or
    /// a crash: the next start reads it from the match's handoff) is recorded for everyone else, with the deleted player's old name kept in the
    /// history and no reward for them. Before, the deleted player failed the whole result at every start.
    /// </summary>
    [Check("server.host-users-delete-recovered-result", TimeoutSeconds = 120)]
    public static void ARecoveredResultSurvivesADeletedPlayer() => RecoveredResultAsync().GetAwaiter().GetResult();

    private static async Task RecoveredResultAsync()
    {
        var root = Directory.CreateTempSubdirectory("league-host-users-recovered").FullName;
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = new GameManager(new HostSettings(root, Environment.ProcessPath!, BindAddress: "127.0.0.1"), Path.Combine(root, "games"));
            var kept = accounts.RegisterPublic("kept1", Password, "Kept One");
            var gone = accounts.RegisterPublic("gone1", Password, "Gone One");
            var seats = MatchRoster.Validate([kept.Loadout with { PlayerId = (int)kept.SummonerId },
                gone.Loadout with { PlayerId = (int)gone.SummonerId, Team = Amberline.Core.Team.Chaos }]);
            var match = Guid.NewGuid();
            accounts.PrepareMatch(new PlatformPendingMatch(accounts.AllocateGameId(), match, 0, [kept.Id, gone.Id], seats));
            // The new process does not know the match (its server ran under the old one), so the delete goes ahead.
            TestAssert.Equal(DeleteOutcome.Deleted, accounts.Delete(gone.Id, _ => false), "gone1 is deleted before the result is recovered");
            var first = Completed(seats[0], kept);
            WriteResult(root, match, first with { Players = [first.Players[0], Completed(seats[1], gone).Players[0]] });

            using var log = new StringWriter();
            TestAssert.Equal(1, accounts.RecoverResults(manager, log), "the result is recovered: " + log);
            var receipt = accounts.ResultsFor(kept.Id).Single();
            TestAssert.True(receipt.HostGameId == match && receipt.Accounts.SequenceEqual([kept.Id, gone.Id]), "the result names both players");
            TestAssert.True(receipt.Rewards is { } rewards && rewards.ContainsKey(kept.Id) && !rewards.ContainsKey(gone.Id), "only kept1 is rewarded");
            TestAssert.Equal("Gone One", receipt.Result.GetProperty("Players")[1].GetProperty("Name").GetString(), "the deleted player's old name stays in the history");
            TestAssert.Equal(match, accounts.ResultsFor(gone.Id).Single().HostGameId, "the deleted player's history keeps the game");
            TestAssert.Equal(0, accounts.RecoverResults(manager, log), "a second start finds nothing left to recover");
        }
        finally { Remove(root); }
    }

    /// <summary>
    /// Review fix 3c: a match history still names players the host deleted since. The client's profile reads of such a player answer with an
    /// empty record under the names and the summoner id it had (as the stats reads answer an unknown id) instead of failing; an id no login ever
    /// had still fails as before.
    /// </summary>
    [Check("server.host-users-deleted-profile-reads", TimeoutSeconds = 120)]
    public static void ProfileReadsOfADeletedPlayerAnswerAnEmptyRecord() => ProfileReadsAsync().GetAwaiter().GetResult();

    private static async Task ProfileReadsAsync()
    {
        var root = Directory.CreateTempSubdirectory("league-host-users-profile").FullName;
        try
        {
            using var accounts = new PlatformAccounts(Path.Combine(root, "accounts"));
            await using var manager = new GameManager(new HostSettings(root, Environment.ProcessPath!, BindAddress: "127.0.0.1"), Path.Combine(root, "games"));
            var games = new PlatformGames(accounts, manager, "127.0.0.1") { Push = (_, _) => { } };
            var sessions = new PlatformSessions(accounts);
            var rpc = new PlatformServices(accounts, sessions, games);
            var viewer = accounts.RegisterPublic("viewer1", Password, "Viewer One");
            var gone = accounts.RegisterPublic("gone1", Password, "Gone One");
            var session = sessions.Login("viewer", viewer.Username, sessions.IssueTicket(viewer), ClientVersion);
            TestAssert.True(sessions.AuthenticateChannel("viewer", viewer.Username, session.Token), "the viewer logs in");
            TestAssert.Equal(DeleteOutcome.Deleted, accounts.Delete(gone.Id), "gone1 is deleted");
            object? Read(string operation, object? argument) =>
                rpc.Route("viewer", operation == "getSpellBook" ? "spellBookService" : "summonerService", operation, [argument]);
            static string Name(object? dto) => (string)((AsObject)dto!)["name"];

            TestAssert.Equal("Gone One", Name(Read("getSummoner", (double)gone.SummonerId)), "getSummoner");
            TestAssert.Equal("Gone One", Name(Read("getSummonerByAccountId", (double)gone.Id)), "getSummonerByAccountId");
            var data = (AsObject)Read("getAllPublicSummonerDataByAccount", (double)gone.Id)!;
            TestAssert.True(Name(data["summoner"]) == "Gone One" && (double)((AsObject)data["summoner"])["sumId"] == gone.SummonerId,
                "getAllPublicSummonerDataByAccount: the old name and summoner id");
            var publics = ((System.Collections.IEnumerable)Read("getPublicSummonersByAccountIds", new object[] { (double)viewer.Id, (double)gone.Id })!)
                .Cast<AsObject>().Select(Name).ToArray();
            TestAssert.Equal("Viewer One | Gone One", string.Join(" | ", publics), "getPublicSummonersByAccountIds");
            TestAssert.Equal((double)gone.SummonerId, ((AsObject)Read("getSpellBook", (double)gone.SummonerId)!)["summonerId"], "getSpellBook");
            TestAssert.Throws<ArgumentException>(() => Read("getSummoner", 9_999_999d), "a summoner id no login ever had still fails");
            TestAssert.Throws<ArgumentException>(() => Read("getSummonerByAccountId", 999_999d), "an account id no login ever had still fails");
        }
        finally { Remove(root); }
    }

    /// <summary>The match server's final result for a match, where it writes it (the shape HostActivityChecks writes).</summary>
    private static void WriteResult(string root, Guid match, MatchResult result)
    {
        var logs = Path.Combine(root, "games", match.ToString("N"), "logs", "session");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "match-result.json"), JsonSerializer.Serialize(new { session_id = match.ToString(), result }));
    }

    private static void Remove(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
