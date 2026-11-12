using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Data;
using Amberline.Host;
using Amberline.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static Amberline.Tests.Air.HostInviteChecks;
using static Amberline.Tests.Air.HostModeChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 751: My account (<c>/account</c>), where a user changes their own password or deletes their own account: on the friends' port
/// whether sign-ups are on or off, and on the local port for the signed-in host only. Temporary folders, free loopback ports (the friends'
/// port on loopback, every 127.x address a different person), made-up logins (<c>host_one</c>, <c>user1</c>, ...) and the reserved name
/// amberline.example.test. The in-a-game cases run this repo's own match server and stop it; no browser, router or client.
/// </summary>
public static class HostAccountPageChecks
{
    private const string TestHost = "amberline.example.test";
    private const string Password = "Synthetic-account-Pw-31";
    private const string NewPassword = "Synthetic-account-New-62";
    private const string Account = HostAccountPage.PagePath;
    private const string NoMatch = "That name and password do not match.";
    private const string Changed = "Your password is changed. Sign in again in the client.";
    private const string NotSignedOut = "Amberline could not finish signing you out everywhere just now. Change your password again in a minute.";
    private const string OwnLogin = "This is the host's own login. It cannot be deleted here.";
    private const string InAGame = "You are in a lobby or a game. Try again once you have left the lobby or the game has ended.";

    /// <summary>
    /// New password: the page on both ports (sign-ups off and on), its links from the sign-up page and Users, the sign-up rule and a mistyped
    /// repeat refused, then the change, which signs the user out of the client (the remote sign-in ends) while the old password stops working.
    /// A user in a running match changes it too: their session ends and the match goes on.
    /// </summary>
    [Check("server.host-account-page-password", TimeoutSeconds = 300)]
    public static void UsersChangeTheirOwnPasswordAndAreSignedOut() => PasswordAsync().GetAwaiter().GetResult();

    private static async Task PasswordAsync()
    {
        await PasswordOnBothPortsAsync();
        await PasswordInAGameAsync();
        await PasswordWhenTheSignOutCannotBeSavedAsync();
    }

    /// <summary>
    /// Review fix 1: the remote sign-in file cannot be written just now (held open), so the revoke is not saved and a restart would read the old
    /// token back. The password still changes and the session ends, and the page says the sign-out is not finished; a second change, once the
    /// file is free, saves the revoke and says nothing more.
    /// </summary>
    private static async Task PasswordWhenTheSignOutCannotBeSavedAsync()
    {
        await using var desk = await Desk.OpenAsync("league-host-account-unsaved");
        var user = desk.Accounts.RegisterPublic("user1", Password, "User One");
        var tunnel = desk.Access.Issue(user.Id).Token;
        desk.LogIn(user);
        var remote = Path.Combine(desk.Root, "data", "remote");
        var token = Token(await desk.Browser.GetStringAsync(Account));
        string done;
        using (new FileStream(Path.Combine(remote, "remote-access.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            done = await PostAsync(desk.Browser, Account, token, HttpStatusCode.OK, Change("user1", Password, NewPassword, NewPassword));
        TestAssert.True(done.Contains(Changed, StringComparison.Ordinal) && done.Contains($"<p class=problem role=alert>{NotSignedOut}</p>", StringComparison.Ordinal),
            "the page says the sign-out is not finished: " + done);
        Public(done, "POST /account (sign-out not saved)", HostPages.Policy);
        TestAssert.True(desk.Accounts.Authenticate("user1", NewPassword) is not null && desk.Sessions.AccountForConnection("rtmp-" + user.Id) is null &&
            desk.Access.AccountFor(tunnel) is null, "the password changed, the session ended and the token is gone from memory");
        TestAssert.Equal(user.Id, new AirRemoteAccess(remote).AccountFor(tunnel), "the file a restart reads still holds the old token");

        var again = await PostAsync(desk.Browser, Account, token, HttpStatusCode.OK, Change("user1", NewPassword, Password, Password));
        TestAssert.True(again.Contains(Changed, StringComparison.Ordinal) && !again.Contains(NotSignedOut, StringComparison.Ordinal), "the second change finishes: " + again);
        TestAssert.True(new AirRemoteAccess(remote).AccountFor(tunnel) is null, "the revoke is saved now");
    }

    private static async Task PasswordOnBothPortsAsync()
    {
        var saved = SavedEnvironment();
        // A new login takes its first champion from the champion list: read it the suite's way first (see HostInviteChecks).
        var champions = ChampionCatalog.All.Count;
        var home = Home.Create("league-host-account-password");
        var data = home.Loop.Paths.Data;
        long hostId, userId;
        using (var store = new PlatformAccounts(data))
        {
            hostId = store.RegisterPublic("host_one", Password, "Host One").Id;
            userId = store.RegisterPublic("user1", Password, "User One").Id;
        }
        var tunnel = new AirRemoteAccess(Path.Combine(data, "remote")).Issue(userId).Token;
        WriteSettings(home, new { serverName = "Test Server", publicHost = TestHost, addressSource = HostAddress.Typed, hostAccount = hostId });
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = HostSessionChecks.Browser(home);
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            using var person = HostSelfRegistrationChecks.Person(home, IPAddress.Loopback);

            // Sign-ups off: My account is on the friends' port all the same, in the sign-up page's look, with its own copy of that stylesheet.
            var form = await FormAsync(person, "the friends' port, sign-ups off");
            using (var sheet = await person.GetAsync(HostPages.AccountStyleHref))
            {
                TestAssert.Equal(HttpStatusCode.OK, sheet.StatusCode, "My account's stylesheet on the friends' port");
                TestAssert.Equal("text/css", sheet.Content.Headers.ContentType?.MediaType);
                TestAssert.True((await sheet.Content.ReadAsStringAsync()).Contains("color-scheme:dark", StringComparison.Ordinal), "the sign-up pages' look");
            }
            using (var off = await person.GetAsync("/register"))
                TestAssert.Equal(HttpStatusCode.NotFound, off.StatusCode, "the sign-up itself stays off");
            // The local port: as /register there, the signed-in host's browser only.
            await FormAsync(browser, "the local port");
            using (var stranger = home.Client())
            using (var refused = await stranger.GetAsync(Account))
                TestAssert.Equal(HttpStatusCode.Forbidden, refused.StatusCode, "the local /account answers only the host's signed-in browser");

            // Sign-ups on: the same page; the sign-up page links it, and Users copies its link.
            await PostAsync(browser, "/host/settings/register", Token(await browser.GetStringAsync(HostSettingsPage.PagePath)), HttpStatusCode.Redirect, ("on", "on"));
            form = await FormAsync(person, "the friends' port, sign-ups on");
            var signUp = await person.GetStringAsync("/register");
            TestAssert.True(signUp.Contains("<p class=already>Already have a login?</p><p><a class=button href=/register/Play.cmd download>Download the Play file</a></p>", StringComparison.Ordinal) &&
                signUp.Contains($"<p><a href={Account}>Change your password or delete your account</a></p></main>", StringComparison.Ordinal), "the sign-up page links My account: " + signUp);
            var users = await browser.GetStringAsync(HostInvite.UsersPath);
            TestAssert.True(users.Contains("<button type=button class=secondary data-copy=account-link data-needs-script hidden>Copy the My account link</button>" +
                $"<span id=account-link class=vh>https://{TestHost}:{home.RemotePort}{Account}</span>", StringComparison.Ordinal), "Users offers the My account link: " + users);

            TestAssert.Equal(HttpStatusCode.OK, await RemoteSignInAsync(browser, home, tunnel), "user1's remote sign-in works before the change");
            // A new password the sign-up rule refuses, and a repeat that differs: each said beside its field; nothing changes, no password comes back.
            var refusal = await PostAsync(person, Account, Token(form), HttpStatusCode.BadRequest, Change("user1", Password, "Short-1", "Short-1"));
            TestAssert.True(refusal.Contains("<a href=#pw-new>Choose a password of at least 10 characters.</a>", StringComparison.Ordinal) &&
                refusal.Contains("id=pw-new-error", StringComparison.Ordinal) && refusal.Contains("value=\"user1\"", StringComparison.Ordinal), refusal);
            NothingBack(refusal, "the sign-up rule", Password, "Short-1");
            Public(refusal, "POST /account (the sign-up rule)", HostPages.Policy);
            refusal = await PostAsync(person, Account, Token(form), HttpStatusCode.BadRequest, Change("user1", Password, NewPassword, NewPassword + "x"));
            TestAssert.True(refusal.Contains("<a href=#pw-again>Type the same new password twice.</a>", StringComparison.Ordinal), refusal);
            NothingBack(refusal, "a repeat that differs", Password, NewPassword);
            await LoginAsync(browser, "user1", Password, HttpStatusCode.OK);

            // The change: the name as typed at sign-in (any case), the page says so, the old password and the remote sign-in end.
            var done = await PostAsync(person, Account, Token(form), HttpStatusCode.OK, Change("User1", Password, NewPassword, NewPassword));
            TestAssert.True(done.Contains($"<p class=notice role=status>{Changed}</p>", StringComparison.Ordinal), done);
            NothingBack(done, "the change", Password, NewPassword);
            Public(done, "POST /account (changed)", HostPages.Policy);
            await LoginAsync(browser, "user1", NewPassword, HttpStatusCode.OK);
            await LoginAsync(browser, "user1", Password, HttpStatusCode.Forbidden);
            TestAssert.Equal(HttpStatusCode.Unauthorized, await RemoteSignInAsync(browser, home, tunnel), "the change ends user1's remote sign-in");
            // The host's own login may change its password here (only its delete is refused).
            done = await PostAsync(person, Account, Token(form), HttpStatusCode.OK, Change("host_one", Password, NewPassword, NewPassword));
            TestAssert.True(done.Contains(Changed, StringComparison.Ordinal), "the host's own login changes its password: " + done);
            await LoginAsync(browser, "host_one", NewPassword, HttpStatusCode.OK);
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        // Never logged: no password in plain text anywhere, the username in no window or log line.
        NeverLogged(home, "user1", Password, NewPassword);
        TestAssert.Equal(champions, ChampionCatalog.All.Count, "the champion list stays as it was");
        Delete(home.Root);
    }

    private static async Task PasswordInAGameAsync()
    {
        await using var desk = await Desk.OpenAsync("league-host-account-game");
        var player = desk.Accounts.RegisterPublic("player1", Password, "Player One");
        var tunnel = desk.Access.Issue(player.Id).Token;
        desk.LogIn(player);
        HostActivityChecks.Solo(desk.Games, desk.Clock, player, "Runs");
        var match = desk.Manager.List(PlatformGames.Guest(player)).Single();
        try
        {
            TestAssert.True(desk.Games.Playing().TryGetValue(player.Id, out var started) && started is not null, "player1 is in a launched match");
            var done = await PostAsync(desk.Browser, Account, Token(await desk.Browser.GetStringAsync(Account)), HttpStatusCode.OK,
                Change("player1", Password, NewPassword, NewPassword));
            TestAssert.True(done.Contains(Changed, StringComparison.Ordinal) && !done.Contains(NotSignedOut, StringComparison.Ordinal),
                "a player in a running match changes their password: " + done);
            TestAssert.True(desk.Accounts.Authenticate("player1", NewPassword) is not null && desk.Accounts.Authenticate("player1", Password) is null, "the new password works");
            TestAssert.True(desk.Sessions.AccountForConnection("rtmp-" + player.Id) is null && desk.Access.AccountFor(tunnel) is null,
                "signed out of the client: the session and the remote sign-in end");
            TestAssert.True(desk.Games.Playing().TryGetValue(player.Id, out var still) && still == started, "the match still holds player1");
            TestAssert.True(desk.Manager.List(PlatformGames.Guest(player)).Single(game => game.Id == match.Id).State is "Starting" or "Waiting for players",
                "the match server still runs the match");
        }
        finally { await desk.Manager.StopAsync(PlatformGames.Guest(player), match.Id); }
    }

    /// <summary>
    /// Delete my account: a box left unticked and the host's own login are refused; then the login goes through the Users page's own Delete
    /// (UserRemoval): its remote sign-in, chat contacts both ways and Cheat commands tick go, the feed says it deleted itself, its history keeps
    /// its names. A user in a lobby is refused and kept; a signed-in user's session ends with the delete.
    /// </summary>
    [Check("server.host-account-page-delete", TimeoutSeconds = 300)]
    public static void UsersDeleteTheirOwnAccountThroughTheUsersPagesDelete() => DeleteAsync().GetAwaiter().GetResult();

    private static async Task DeleteAsync()
    {
        await DeleteOnTheFriendsPortAsync();
        await DeleteHeldByALobbyAsync();
    }

    private static async Task DeleteOnTheFriendsPortAsync()
    {
        var saved = SavedEnvironment();
        var champions = ChampionCatalog.All.Count;
        var home = Home.Create("league-host-account-delete");
        var data = home.Loop.Paths.Data;
        long hostId, victimId, otherId;
        using (var store = new PlatformAccounts(data))
        {
            hostId = store.RegisterPublic("host_one", Password, "Host One").Id;
            victimId = store.RegisterPublic("victim1", Password, "Victim One").Id;
            otherId = store.RegisterPublic("other1", Password, "Other One").Id;
        }
        using (var social = new PlatformSocialStore(Path.Combine(data, "social")))
        {
            social.SetContact(victimId, otherId, "General", "both", false);
            social.SetContact(otherId, victimId, "General", "both", false);
            social.SetContact(otherId, hostId, "General", "both", false);
        }
        var remote = new AirRemoteAccess(Path.Combine(data, "remote"));
        var victimToken = remote.Issue(victimId).Token;
        var otherToken = remote.Issue(otherId).Token;
        WriteSettings(home, new { serverName = "Test Server", publicHost = TestHost, addressSource = HostAddress.Typed, hostAccount = hostId,
            cheatAccounts = new[] { hostId, victimId, otherId } });
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = HostSessionChecks.Browser(home);
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            using var person = HostSelfRegistrationChecks.Person(home, IPAddress.Loopback);
            var form = await FormAsync(person, "the friends' port");

            var refused = await PostAsync(person, Account, Token(form), HttpStatusCode.BadRequest, Remove("victim1", Password, ticked: false));
            TestAssert.True(refused.Contains("<a href=#del-confirm>Tick the box to confirm that you want to delete your account.</a>", StringComparison.Ordinal) &&
                refused.Contains("id=del-confirm-error", StringComparison.Ordinal), "the box unticked: " + refused);
            NothingBack(refused, "the box unticked", Password);
            Public(refused, "POST /account (unticked)", HostPages.Policy);
            refused = await PostAsync(person, Account, Token(form), HttpStatusCode.BadRequest, Remove("host_one", Password, ticked: true));
            TestAssert.True(refused.Contains(WebUtility.HtmlEncode(OwnLogin), StringComparison.Ordinal), "the host's own login: " + refused);
            await LoginAsync(browser, "host_one", Password, HttpStatusCode.OK);
            await LoginAsync(browser, "victim1", Password, HttpStatusCode.OK);

            var done = await PostAsync(person, Account, Token(form), HttpStatusCode.OK, Remove("Victim1", Password, ticked: true));
            TestAssert.True(done.Contains("<p class=notice role=status>Your account is deleted.", StringComparison.Ordinal), done);
            NothingBack(done, "the delete", Password);
            Public(done, "POST /account (deleted)", HostPages.Policy);
            TestAssert.True(home.Loop.Activity.Recent().Any(entry => entry.Text == "victim1 deleted their own account"), "the feed says the user deleted their own account");
            await LoginAsync(browser, "victim1", Password, HttpStatusCode.Forbidden);
            TestAssert.False((await browser.GetStringAsync(HostInvite.UsersPath)).Contains("<b>victim1</b>", StringComparison.Ordinal), "Users no longer lists it");
            // Gone: a second try is a name that does not match, like any other.
            refused = await PostAsync(person, Account, Token(form), HttpStatusCode.BadRequest, Remove("victim1", Password, ticked: true));
            TestAssert.True(refused.Contains(NoMatch, StringComparison.Ordinal), refused);
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        using (var store = new PlatformAccounts(data))
            TestAssert.True(store.Find(victimId) is null && store.FindDeleted(victimId) is { Username: "victim1", Summoner: "Victim One" } &&
                store.Find(hostId) is not null && store.Find(otherId) is not null, "deleted, its names kept for history; the others stay");
        using (var social = new PlatformSocialStore(Path.Combine(data, "social")))
            TestAssert.True(social.Contacts(victimId).Length == 0 && social.Contacts(otherId).Single().ContactId == hostId, "its chat contacts are gone, both ways");
        var tokens = new AirRemoteAccess(Path.Combine(data, "remote"));
        TestAssert.True(tokens.AccountFor(victimToken) is null && tokens.AccountFor(otherToken) == otherId, "only its remote sign-in is gone");
        using (var json = JsonDocument.Parse(File.ReadAllText(home.Loop.Paths.Settings)))
            TestAssert.Equal($"{hostId},{otherId}", string.Join(",", json.RootElement.GetProperty("cheatAccounts").EnumerateArray().Select(value => value.GetInt64())),
                "its Cheat commands tick is gone");
        NeverLogged(home, "victim1", Password);
        TestAssert.Equal(champions, ChampionCatalog.All.Count, "the champion list stays as it was");
        Delete(home.Root);
    }

    private static async Task DeleteHeldByALobbyAsync()
    {
        await using var desk = await Desk.OpenAsync("league-host-account-lobby");
        var player = desk.Accounts.RegisterPublic("lobby1", Password, "Lobby One");
        desk.Games.Create(player, "Waiting", "", 1, 2, AirGameDtos.BlindPickConfigId, "CLASSIC", "NONE");
        var token = Token(await desk.Browser.GetStringAsync(Account));
        var refused = await PostAsync(desk.Browser, Account, token, HttpStatusCode.Conflict, Remove("lobby1", Password, ticked: true));
        TestAssert.True(refused.Contains(InAGame, StringComparison.Ordinal), "a lobby's member: " + refused);
        TestAssert.True(desk.Accounts.Find(player.Id) is not null && !desk.Loop.Activity.Recent().Any(entry => entry.Text.Contains("deleted", StringComparison.Ordinal)),
            "nothing was deleted");

        // Out of the lobby and signed in: the delete ends the session at once, as the Users page's Delete does.
        desk.Games.Quit(player.Id);
        desk.LogIn(player);
        await PostAsync(desk.Browser, Account, token, HttpStatusCode.OK, Remove("lobby1", Password, ticked: true));
        TestAssert.True(desk.Accounts.Find(player.Id) is null && desk.Sessions.AccountForConnection("rtmp-" + player.Id) is null, "the login and its session are gone");
        TestAssert.Equal("lobby1 deleted their own account | lobby1 logged out", string.Join(" | ", desk.Loop.Activity.Recent(2).Select(entry => entry.Text)),
            "the feed names the logout and the delete");
    }

    /// <summary>
    /// What My account refuses: a wrong password and an unknown name get the same message and status (after the same password check, so
    /// about the same time), on either form; a disabled login; a form without its token. Every POST counts: the 11th in an hour from one
    /// address and the 61st from all addresses together get the plain 429 page, in the page's policy and never stored.
    /// </summary>
    [Check("server.host-account-page-refusals", TimeoutSeconds = 300)]
    public static void MyAccountRefusesGuessesAndFloods() => RefusalsAsync().GetAwaiter().GetResult();

    private static async Task RefusalsAsync()
    {
        await RefusalsAndAddressCapAsync();
        await CapInAllAsync();
    }

    private static async Task RefusalsAndAddressCapAsync()
    {
        var saved = SavedEnvironment();
        var champions = ChampionCatalog.All.Count;
        var home = Home.Create("league-host-account-refusals");
        long hostId;
        using (var store = new PlatformAccounts(home.Loop.Paths.Data))
        {
            hostId = store.RegisterPublic("host_one", Password, "Host One").Id;
            store.RegisterPublic("user1", Password, "User One");
            store.RemoveAccess(store.RegisterPublic("off1", Password, "Off One").Id);
            // Batch 751 final review: a host Disable that lands between the user's password check and the change wins, under the store's lock.
            var raced = store.RegisterPublic("race1", Password, "Race One");
            store.RemoveAccess(raced.Id);
            TestAssert.False(store.ChangeOwnPassword(raced.Id, NewPassword), "a disabled login's own change is refused");
            TestAssert.True(store.Find(raced.Id)?.AccessRemovedAt is not null && store.Authenticate("race1", NewPassword) is null, "the login stays disabled");
        }
        WriteSettings(home, new { serverName = "Test Server", publicHost = TestHost, addressSource = HostAddress.Typed, hostAccount = hostId });
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = HostSessionChecks.Browser(home);
            await WaitForHealthAsync(browser, "ready", run);

            // One address: eight POSTs, within its ten.
            using (var person = HostSelfRegistrationChecks.Person(home, IPAddress.Parse("127.0.0.2")))
            {
                var token = Token(await FormAsync(person, "the friends' port"));
                var wrong = new List<(HttpStatusCode Status, string Page, TimeSpan Took)>();
                var unknown = new List<(HttpStatusCode Status, string Page, TimeSpan Took)>();
                for (var round = 0; round < 2; round++)
                {
                    wrong.Add(await TimedAsync(person, token, Change("user1", "Wrong-password-11", NewPassword, NewPassword)));
                    unknown.Add(await TimedAsync(person, token, Change("nobody1", Password, NewPassword, NewPassword)));
                }
                var deleteWrong = await TimedAsync(person, token, Remove("user1", "Wrong-password-11", ticked: true));
                var deleteUnknown = await TimedAsync(person, token, Remove("nobody1", Password, ticked: true));
                var disabled = await TimedAsync(person, token, Change("off1", Password, NewPassword, NewPassword));
                foreach (var (answer, what) in wrong.Select(answer => (answer, "a wrong password")).Concat(unknown.Select(answer => (answer, "an unknown name")))
                             .Append((deleteWrong, "Delete, a wrong password")).Append((deleteUnknown, "Delete, an unknown name")).Append((disabled, "a disabled login")))
                {
                    TestAssert.Equal(HttpStatusCode.BadRequest, answer.Status, what + ": " + answer.Page);
                    TestAssert.True(answer.Page.Contains($"{NoMatch}</a></li>", StringComparison.Ordinal), $"{what}: the one message: {answer.Page}");
                    NothingBack(answer.Page, what, Password, NewPassword, "Wrong-password-11");
                    Public(answer.Page, what, HostPages.Policy);
                }
                TestAssert.Equal(Summary(wrong[0].Page), Summary(unknown[0].Page), "a wrong password and an unknown name read the same");
                TestAssert.Equal(Summary(deleteWrong.Page), Summary(deleteUnknown.Page), "on Delete too");
                TestAssert.Equal(Summary(wrong[0].Page), Summary(disabled.Page), "a disabled login reads the same");
                // The same password check either way: an unknown name costs about what a wrong password does (no early answer to time).
                var fastestWrong = wrong.Min(answer => answer.Took);
                var fastestUnknown = unknown.Min(answer => answer.Took);
                TestAssert.True(fastestUnknown >= fastestWrong / 4, $"an unknown name took {fastestUnknown.TotalMilliseconds} ms, a wrong password {fastestWrong.TotalMilliseconds} ms");

                var expired = await PostAsync(person, Account, null, HttpStatusCode.BadRequest, Change("user1", Password, NewPassword, NewPassword));
                TestAssert.True(expired.Contains("The page expired. Please try again.", StringComparison.Ordinal), "a form without its token: " + expired);
                NothingBack(expired, "a form without its token", Password, NewPassword);
            }
            await LoginAsync(browser, "user1", Password, HttpStatusCode.OK);
            await LoginAsync(browser, "off1", Password, HttpStatusCode.Forbidden);

            // Ten POSTs an hour from one address, every one counted; the 11th gets the plain page, a right password too. Another address goes on.
            using (var flood = HostSelfRegistrationChecks.Person(home, IPAddress.Parse("127.0.0.3")))
            {
                var token = Token(await flood.GetStringAsync(Account));
                for (var i = 1; i <= 10; i++)
                    TestAssert.Equal(HttpStatusCode.BadRequest, (await TimedAsync(flood, token, Change("user1", "Wrong-password-11", NewPassword, NewPassword))).Status, $"POST {i} of 10");
                TooMany(await RawAsync(flood, token, Change("user1", Password, NewPassword, NewPassword)), "the 11th POST from one address in an hour");
            }
            await LoginAsync(browser, "user1", Password, HttpStatusCode.OK);
            using (var next = HostSelfRegistrationChecks.Person(home, IPAddress.Parse("127.0.0.4")))
                await PostAsync(next, Account, null, HttpStatusCode.BadRequest, Change("user1", Password, NewPassword, NewPassword));
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        NeverLogged(home, "user1", Password, NewPassword);
        TestAssert.Equal(champions, ChampionCatalog.All.Count, "the champion list stays as it was");
        Delete(home.Root);
    }

    /// <summary>A Platform of its own: six addresses use their ten POSTs each, so a seventh address's first POST is the 61st of the hour.</summary>
    private static async Task CapInAllAsync()
    {
        var saved = SavedEnvironment();
        var champions = ChampionCatalog.All.Count;
        var home = Home.Create("league-host-account-cap");
        long hostId;
        using (var store = new PlatformAccounts(home.Loop.Paths.Data)) hostId = store.RegisterPublic("host_one", Password, "Host One").Id;
        WriteSettings(home, new { serverName = "Test Server", publicHost = TestHost, addressSource = HostAddress.Typed, hostAccount = hostId });
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = HostSessionChecks.Browser(home);
            await WaitForHealthAsync(browser, "ready", run);
            for (var last = 2; last <= 7; last++)
            {
                using var person = HostSelfRegistrationChecks.Person(home, IPAddress.Parse($"127.0.0.{last}"));
                for (var i = 1; i <= 10; i++)
                    await PostAsync(person, Account, null, HttpStatusCode.BadRequest, Change("user1", Password, NewPassword, NewPassword));
            }
            using var late = HostSelfRegistrationChecks.Person(home, IPAddress.Parse("127.0.0.8"));
            TooMany(await RawAsync(late, null, Change("user1", Password, NewPassword, NewPassword)), "the 61st POST in an hour, from a new address");
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        TestAssert.Equal(champions, ChampionCatalog.All.Count, "the champion list stays as it was");
        Delete(home.Root);
    }

    /// <summary>GET /account: both forms with their tokens, the sign-up pages' look, policy and no-store, nothing of the host's.</summary>
    private static async Task<string> FormAsync(HttpClient client, string where)
    {
        using var answer = await client.GetAsync(Account);
        var page = await answer.Content.ReadAsStringAsync();
        TestAssert.Equal(HttpStatusCode.OK, answer.StatusCode, $"GET {Account} on {where}: {page}");
        var policy = string.Join(";", answer.Headers.GetValues("Content-Security-Policy"));
        TestAssert.Equal(HostPages.Policy, policy, $"{where}: the sign-up pages' policy");
        TestAssert.True(answer.Headers.CacheControl?.NoStore == true, $"{where}: no-store");
        Public(page, $"GET {Account} on {where}", policy);
        TestAssert.Equal(2, Regex.Count(page, $"<form method=post action={Account} novalidate><input type=hidden name=\"[^\"]+\" value=\"[^\"]+\">"),
            $"{where}: two forms, each with its antiforgery token: {page}");
        TestAssert.True(page.Contains("<input type=hidden name=action value=password>", StringComparison.Ordinal) &&
            page.Contains("<input type=hidden name=action value=delete>", StringComparison.Ordinal) &&
            page.Contains("I understand this deletes my login and cannot be undone", StringComparison.Ordinal) &&
            page.Contains("<button type=submit>Change password</button>", StringComparison.Ordinal) &&
            page.Contains("<button type=submit>Delete my account</button>", StringComparison.Ordinal), $"{where}: New password and Delete my account: {page}");
        return page;
    }

    /// <summary>A public page: the host pages' standard (batch 652) at My account's stylesheet, and no menu, no /host link, no script.</summary>
    private static void Public(string page, string what, string policy)
    {
        HostPagesChecks.Accessible(page, what, policy, HostPages.AccountStyleHref);
        TestAssert.False(page.Contains("<nav", StringComparison.Ordinal) || page.Contains("/host", StringComparison.Ordinal) || page.Contains("<script", StringComparison.Ordinal),
            $"{what}: no host menu, no link to a host page and no script: {page}");
    }

    /// <summary>The 429 page: plain words, the page's policy, never stored, nothing of the host's.</summary>
    private static void TooMany((HttpStatusCode Status, string Page, string Policy, bool NoStore) answer, string why)
    {
        TestAssert.Equal(HttpStatusCode.TooManyRequests, answer.Status, why + ": " + answer.Page);
        TestAssert.True(answer.Page.Contains("<h1>Too many tries</h1>", StringComparison.Ordinal) && answer.Page.Contains("Try again in an hour.", StringComparison.Ordinal) &&
            !answer.Page.Contains("Exception", StringComparison.Ordinal), why + ", in plain words: " + answer.Page);
        TestAssert.Equal(HostPages.Policy, answer.Policy, why + ": the page's policy");
        TestAssert.True(answer.NoStore, why + ": no-store");
        NothingBack(answer.Page, why, Password, NewPassword);
        Public(answer.Page, why, answer.Policy);
    }

    private static (string Name, string Value)[] Change(string username, string password, string next, string again) =>
        [("action", "password"), ("username", username), ("password", password), ("new", next), ("again", again)];

    private static (string Name, string Value)[] Remove(string username, string password, bool ticked) => ticked
        ? [("action", "delete"), ("username", username), ("password", password), ("confirm", "yes")]
        : [("action", "delete"), ("username", username), ("password", password)];

    private static async Task<(HttpStatusCode Status, string Page, TimeSpan Took)> TimedAsync(HttpClient client, KeyValuePair<string, string> token,
        (string Name, string Value)[] fields)
    {
        var clock = Stopwatch.StartNew();
        var (status, page, _, _) = await RawAsync(client, token, fields);
        return (status, page, clock.Elapsed);
    }

    private static async Task<(HttpStatusCode Status, string Page, string Policy, bool NoStore)> RawAsync(HttpClient client, KeyValuePair<string, string>? token,
        (string Name, string Value)[] fields)
    {
        var values = fields.ToDictionary(field => field.Name, field => field.Value);
        if (token is { } field) values[field.Key] = field.Value;
        using var answer = await client.PostAsync(Account, new FormUrlEncodedContent(values));
        return (answer.StatusCode, await answer.Content.ReadAsStringAsync(),
            answer.Headers.TryGetValues("Content-Security-Policy", out var policy) ? string.Join(";", policy) : "", answer.Headers.CacheControl?.NoStore == true);
    }

    /// <summary>The page's error summary (what it says went wrong), for comparing two refusals.</summary>
    private static string Summary(string page) => Regex.Match(page, "<div class=error-summary.*?</div>", RegexOptions.Singleline).Value;

    private static void NothingBack(string page, string what, params string[] secrets)
    {
        foreach (var secret in secrets) TestAssert.False(page.Contains(secret, StringComparison.Ordinal), $"{what}: a password never comes back");
    }

    /// <summary>The friends' port's remote sign-in check with <paramref name="token"/> (the companion's, before any tunnel).</summary>
    private static async Task<HttpStatusCode> RemoteSignInAsync(HttpClient client, Home home, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{home.RemotePort}{AirRemoteTunnel.SignInPath}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var answer = await client.SendAsync(request);
        return answer.StatusCode;
    }

    /// <summary>No password in plain text in any file or the window; the username in no window text or log file.</summary>
    private static void NeverLogged(Home home, string username, params string[] passwords)
    {
        NeverStored(home.Loop.Paths.Data, home.Screen.ToString(), passwords);
        TestAssert.False(home.Screen.ToString().Contains(username, StringComparison.OrdinalIgnoreCase), "the window never names " + username);
        foreach (var log in Directory.EnumerateFiles(home.Root, "*.log", SearchOption.AllDirectories))
            TestAssert.False(File.ReadAllText(log).Contains(username, StringComparison.OrdinalIgnoreCase), $"no log names {username}: {log}");
    }

    private static Dictionary<string, string?> SavedEnvironment() =>
        new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);

    /// <summary>
    /// My account on a web app of its own over the stores, sessions, games (with this repo's match server) and chat this check holds, so a
    /// check can sign a user in or seat them in a lobby or a match. Only the page is mapped: the full Platform's gate and limits are the
    /// other checks'. <c>host_one</c> is the host's own login.
    /// </summary>
    private sealed class Desk : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required HostLoop Loop { get; init; }
        public required PlatformAccounts Accounts { get; init; }
        public required PlatformSessions Sessions { get; init; }
        public required AirRemoteAccess Access { get; init; }
        public required GameManager Manager { get; init; }
        public required PlatformGames Games { get; init; }
        public required HostActivityChecks.Clock Clock { get; init; }
        public required AirXmppHost Chat { get; init; }
        public required HostAccountPage Page { get; init; }
        public required WebApplication App { get; init; }
        public required HttpClient Browser { get; init; }

        public static async Task<Desk> OpenAsync(string prefix)
        {
            var root = AirBootstrapChecks.TemporaryDirectory(prefix);
            var paths = HostPaths.Resolve(Path.Combine(root, "data"), Path.Combine(root, "Local"), Path.Combine(root, "app", "platform"));
            Directory.CreateDirectory(paths.Data);
            var accounts = new PlatformAccounts(paths.Data);
            var host = accounts.RegisterPublic("host_one", Password, "Host One");
            File.WriteAllText(paths.Settings, JsonSerializer.Serialize(new { hostAccount = host.Id }));
            var loop = new HostLoop(paths, _ => { }, TextWriter.Null);
            var clock = new HostActivityChecks.Clock();
            var sessions = new PlatformSessions(accounts, activity: loop.Activity);
            using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var gamePort = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
            probe.Close();
            var manager = new GameManager(new HostSettings(GameData.Default.Paths.Root, HostActivityChecks.ServerExecutable(),
                FirstGamePort: gamePort, MaxGames: 1, BindAddress: "127.0.0.1"), Path.Combine(root, "games"));
            var games = new PlatformGames(accounts, manager, "127.0.0.1", clock, loop.Activity) { Push = (_, _) => { } };
            var chat = new AirXmppHost(accounts, sessions, Path.Combine(paths.Data, "social"), 0);
            var access = new AirRemoteAccess(Path.Combine(paths.Data, "remote"));
            var page = new HostAccountPage(loop, accounts, sessions, access, games, chat, remotePort: 0);
            var port = AirManagementChecks.FreePort();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
            builder.Services.AddAntiforgery(options => options.Cookie.Name = "Amberline.Forms");
            var app = builder.Build();
            page.Map(app);
            await app.StartAsync();
            var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true, CookieContainer = new CookieContainer(), UseProxy = false })
                { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
            return new Desk
            {
                Root = root, Loop = loop, Accounts = accounts, Sessions = sessions, Access = access, Manager = manager, Games = games, Clock = clock,
                Chat = chat, Page = page, App = app, Browser = browser,
            };
        }

        /// <summary>A live login on its own connection, <c>rtmp-&lt;id&gt;</c>, as the stock client's login and channel login make it.</summary>
        public void LogIn(PlatformAccount account)
        {
            var session = Sessions.Login("rtmp-" + account.Id, account.Username, Sessions.IssueTicket(account), "1.45.11_10_04_15_28");
            TestAssert.True(Sessions.AuthenticateChannel("rtmp-" + account.Id, account.Username, session.Token), account.Username + " logs in");
        }

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            Browser.Dispose();
            Page.Dispose();
            await Chat.DisposeAsync();
            await Manager.DisposeAsync();
            Accounts.Dispose();
            HostModeChecks.Delete(Root);
        }
    }
}
