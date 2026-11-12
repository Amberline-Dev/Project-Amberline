using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: Users and Add a user (Amberline Host design, section 2.4; batch 748 made Invite a friend and Friends one page; retail players
/// signed up on Riot's website and downloaded Riot's installer). Under <c>/host</c>, behind <see cref="HostGate"/>. Add a user makes the
/// login with <see cref="PlatformAccounts.RegisterPublic"/> and a generated password shown once (never logged, never stored in plain text),
/// and hands out the one Play file, rendered from the template beside the Platform (<c>tools/play-bootstrap.cmd</c>) with the
/// server's name and address. Users lists every login but the host's own (<c>hostAccount</c>), with its marks (Signed up, Disabled), its
/// status (in a game, online, last seen) and its actions, each ending the user's sign-in token and sessions: New password; Disable, a
/// password nobody knows that deletes nothing (<see cref="PlatformAccounts.RemoveAccess"/>), and Enable, a new password that lets a
/// disabled user in again; and Delete, behind its confirm page, which removes the login for good while history keeps its names
/// (<see cref="PlatformAccounts.Delete"/>). Invite a friend's and Friends' old addresses lead to the new ones.
/// </summary>
public static class HostInvite
{
    public const string UsersPath = "/host/users", AddPath = "/host/users/add", PlayFilePath = "/host/play-file";
    /// <summary>Batch 748: the old pages' addresses, Invite a friend and Friends; a GET goes on to Add a user and Users (303).</summary>
    public const string InvitePath = "/host/invite", FriendsPath = "/host/friends";
    /// <summary>The Play-file template's name beside the Platform (<c>tools/build-server.ps1 -Layout Home</c> puts it there).</summary>
    public const string TemplateFile = "play-bootstrap.cmd";
    /// <summary>The downloaded file's name, as batch 619 names it.</summary>
    public const string PlayFileName = "Play.cmd";
    /// <summary>What the dashboard and Add a user say while the host chose Only people on my home network (<see cref="HostAddress.Lan"/>).</summary>
    public const string HomeNetworkNote = "Players must be on the same home network as this PC.";
    /// <summary><c>tools/player-file.ps1</c>'s default for a server without a name.</summary>
    internal const string DefaultName = "Amberline";
    private const string Expired = "The page expired. Please try again.";

    // Common words for generated passwords: four of them and two digits (about 39 bits), typed into the League client's login.
    private static readonly string[] Words = ("able acorn actor agent alarm album alley amber angle apple apron arena arrow atlas attic award " +
        "bacon badge bagel baker bamboo banjo barn basil basket beach beard bell bench berry bike bird blade blaze bloom board boat " +
        "bonus book boot bottle brain branch brave bread brick bridge brook brush bucket cabin cable cactus cake camel camera camp " +
        "candle candy canoe canyon cape cargo carpet carrot castle cedar chair chalk charm cheese cherry chess chief cider circle city " +
        "cloud clover coach coast cocoa coffee comet coral corn cotton couch crane crayon cream creek crown cup curtain daisy dance " +
        "delta denim desert diary dinner dolphin donut dove dragon dream drum eagle earth echo elbow ember engine falcon farm feather " +
        "fence fern field finch flame flute forest fossil fox frost garden garlic gate giant ginger glove goat gold grape grass guitar " +
        "hammer harbor harp hat hazel helmet hill honey horse island ivory jacket jelly jungle kayak kettle kite koala ladder lake " +
        "lamp lemon lily lion llama lobby lunar magnet mango maple marble meadow melon mint mirror moon moss motor mountain mouse " +
        "muffin music nectar noodle novel oak ocean olive onion orange orbit otter owl paddle panda paper parrot peach pearl pencil " +
        "pepper piano pillow pine pirate planet plum pocket pony poppy potato prism pumpkin puzzle quartz quiet rabbit radio rain " +
        "raven ribbon river robin rocket rose ruby saddle salmon sand scarf shell silver sky slate snow sock spoon spruce star stone " +
        "storm sugar summer sun swan table tiger toast tomato topaz tower train tulip turtle valley velvet violet wagon walnut water " +
        "whale willow window winter wolf wood yarn zebra").Split(' ');

    /// <summary>A new password: four common words and two digits, for example <c>river-candle-otter-maple-42</c>.</summary>
    public static string NewPassword() =>
        string.Join('-', Enumerable.Range(0, 4).Select(_ => Words[RandomNumberGenerator.GetInt32(Words.Length)])) +
        "-" + RandomNumberGenerator.GetInt32(100).ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>
    /// The host's pages for users. <paramref name="publicHost"/> and <paramref name="serverName"/> are read on each request
    /// (<see cref="HostSteps"/> changes them without a restart); the Play file pins <paramref name="certificateSha256"/> on
    /// <paramref name="remotePort"/>, as <c>/bootstrap/client.json</c> does. Batch 656: while <paramref name="signUp"/>'s switch is on,
    /// Add a user also shows its link (and Users its Copy sign-up link). Batch 748: <paramref name="games"/> gives the status In game and
    /// keeps Delete from a user in a game (without it the store's own rule decides), and <paramref name="chat"/> forgets a deleted user.
    /// </summary>
    public static void Map(WebApplication app, HostLoop loop, Func<string?> publicHost, Func<string?> serverName, PlatformAccounts accounts,
        PlatformSessions sessions, AirRemoteAccess remoteAccess, int remotePort, string certificateSha256, HostSelfRegistration? signUp = null,
        PlatformGames? games = null, AirXmppHost? chat = null)
    {
        string Name() => serverName() ?? DefaultName;
        // Batch 657: a failed read must not drop the host's id, or Users would list the host's own login and let a button change it.
        long? HostAccount() => HostMode.ReadStrict(loop.Paths.Settings).HostAccount;
        bool HomeOnly() => HostMode.ReadOrDefault(loop.Paths.Settings).AddressSource == HostAddress.Lan;
        string Link() => signUp?.Link(publicHost()) is { Length: > 0 } link
            ? $"<h2>Or send this link</h2><p>Anyone with it can make their own login.</p>{link}" : "";
        // Batch 748: the old pages' addresses (a bookmark, an open tab) go on to the new pages; their forms' addresses are gone.
        app.MapGet(InvitePath, (HttpContext context) => SeeOther(context, AddPath));
        app.MapGet(FriendsPath, (HttpContext context) => SeeOther(context, UsersPath));
        app.MapGet(AddPath, (HttpContext context, IAntiforgery antiforgery) => publicHost() is null ? AddressFirst()
            : HostMode.Page(AddForm(Program.RegistrationToken(context, antiforgery), [], "", "", "maxed", HomeOnly(), Link())));
        app.MapPost(AddPath, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (publicHost() is null) return AddressFirst();
            var form = await FormAsync(context, antiforgery);
            var username = form?["username"].ToString() ?? "";
            var summoner = form?["summoner"].ToString() ?? "";
            var mode = form?["mode"].ToString() ?? "maxed";
            // The host may type a password; otherwise Amberline makes one. Either way it is only hashed, and shown once below.
            var typed = form?["password"].ToString() ?? "";
            // Batch 650: the summoner name is the username when its field is empty, as RegisterPublic stores it, so that is checked.
            var stored = summoner.Trim().Length == 0 ? username : summoner;
            List<string> problems = form is null ? [Expired] : Problems(username, typed, stored, mode);
            if (problems.Count == 0)
            {
                var password = typed.Length == 0 ? NewPassword() : typed;
                try
                {
                    var account = accounts.RegisterPublic(username, password, stored, maxed: mode == "maxed");
                    // NOT RETAIL: the host page's activity feed names the user the host added (batch 748), never the password.
                    loop.Activity.Add($"{account.Username} added");
                    return HostMode.Page(Ready(account.Username, password, Name(), HomeOnly()));
                }
                catch (ArgumentException ex) { problems = [ex.Message]; }
            }
            return HostMode.Page(AddForm(Program.RegistrationToken(context, antiforgery), problems, username, summoner, mode, HomeOnly(), Link()),
                StatusCodes.Status400BadRequest);
        });
        // Batch 650: every problem at once, in the first-run login step's order (batch 649): the store's refusals of the username, of
        // the password (only one the host typed) and of the summoner name, a name already taken, then the start choice.
        List<string> Problems(string username, string typed, string stored, string mode)
        {
            var (name, secret, nick) = PlatformAccounts.Refusals(username, typed, stored);
            // An empty summoner field takes the username, whose own rule is the stricter one, so only its message shows.
            return [.. new[] { name, typed.Length == 0 ? null : secret, stored == username ? null : nick, accounts.TakenRefusal(username, stored),
                mode is "maxed" or "fresh" ? null : "Choose where their login starts." }.OfType<string>()];
        }
        app.MapGet(PlayFilePath, () =>
        {
            if (!File.Exists(Template(loop)))
                return HostMode.Page("<h1>The Play file is missing</h1><p>play-bootstrap.cmd is not in Amberline's folder " +
                    $"({WebUtility.HtmlEncode(loop.Paths.App)}). Download Amberline again and start it.</p>",
                    StatusCodes.Status404NotFound);
            if (publicHost() is not { } host) return AddressFirst();
            return PlayFile(loop, host, remotePort, certificateSha256, Name()) ?? Results.NotFound();
        });

        app.MapGet(UsersPath, (HttpContext context, IAntiforgery antiforgery) => HostMode.Page(UsersPage(Program.RegistrationToken(context, antiforgery))));
        // New password: shown once. A user disabled meanwhile (a page open from before) is let in only by Enable.
        app.MapPost(UsersPath + "/password", (HttpContext context, IAntiforgery antiforgery) => ActAsync(context, antiforgery, (user, token) =>
        {
            if (user.AccessRemovedAt is not null)
                return HostMode.Page(UsersPage(token, $"{user.Username} is disabled. Enable gives them a new password."), StatusCodes.Status409Conflict);
            var password = NewPassword();
            accounts.SetPassword(user.Id, password);
            return HostMode.Page(NewPasswordPage(user.Username, password, Name(), again: false, LogOut(user.Id)));
        }));
        // Disable (batch 646's Remove access): a password nobody is told, kept on record, so no login works until Enable; the account and
        // its games stay.
        app.MapPost(UsersPath + "/disable", (HttpContext context, IAntiforgery antiforgery) => ActAsync(context, antiforgery, (user, token) =>
        {
            if (user.AccessRemovedAt is not null) return HostMode.Page(UsersPage(token, $"{user.Username} is already disabled.", done: true));
            accounts.RemoveAccess(user.Id);
            var unsaved = LogOut(user.Id);
            ClearCheats(user.Id);
            // NOT RETAIL: the host page's activity feed names the host's Disable, Enable and Delete (batch 748).
            loop.Activity.Add($"{user.Username} disabled");
            return HostMode.Page(UsersPage(token, $"{user.Username} can no longer log in, and was logged out. Their games stay.", done: true, unsaved));
        }));
        // Enable (Give access again): a new password, shown once, which also ends the removal.
        app.MapPost(UsersPath + "/enable", (HttpContext context, IAntiforgery antiforgery) => ActAsync(context, antiforgery, (user, token) =>
        {
            if (user.AccessRemovedAt is null) return HostMode.Page(UsersPage(token, $"{user.Username} can already log in.", done: true));
            var password = NewPassword();
            accounts.SetPassword(user.Id, password);
            var unsaved = LogOut(user.Id);
            ClearCheats(user.Id);
            loop.Activity.Add($"{user.Username} enabled");
            return HostMode.Page(NewPasswordPage(user.Username, password, Name(), again: true, unsaved));
        }));
        // Delete: its confirm page (a GET deletes nothing), then the delete. A user in a lobby or a game is not deleted (PlatformGames.DeleteAccount).
        app.MapGet(UsersPath + "/delete", (HttpContext context, IAntiforgery antiforgery, string? id) =>
        {
            var (user, refused) = Chosen(id);
            return user is null ? refused! : HostMode.Page(DeleteConfirm(Program.RegistrationToken(context, antiforgery), user));
        });
        app.MapPost(UsersPath + "/delete", (HttpContext context, IAntiforgery antiforgery) => ActAsync(context, antiforgery, (user, token) =>
        {
            switch (games?.DeleteAccount(user.Id) ?? accounts.Delete(user.Id))
            {
                case DeleteOutcome.NotFound: return Gone;
                case DeleteOutcome.InGame:
                    return HostMode.Page($"<h1>{WebUtility.HtmlEncode(user.Username)} is in a lobby or a game</h1><p>Try again once they have left the lobby or the game has ended.</p>" +
                        $"<p><a href={UsersPath}>Back to Users</a></p>", StatusCodes.Status409Conflict);
            }
            // The login is gone; history keeps its names (DeletedAccount). Its sessions end first, in memory, so nothing below can leave it
            // logged in; then its chat streams and each file that names it are tidied in steps of their own, so one that cannot be saved just
            // now does not keep the others, and the page says which.
            sessions.EndAccount(user.Id);
            loop.Activity.Add($"{user.Username} deleted");
            var unsaved = new List<string>();
            void Tidy(string what, Action step)
            {
                try { step(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SettingsUnreadableException)
                {
                    // The log names no user: it reaches the support log, which a host may paste in public.
                    Console.Error.WriteLine($"Amberline deleted a user but could not save {what}: {ex.Message}");
                    unsaved.Add(what);
                }
            }
            Tidy("their remote sign-in", () => remoteAccess.Revoke(user.Id));
            Tidy("their chat contacts", () => chat?.Forget(user.Id));
            Tidy("the Cheat commands list", () => ClearCheats(user.Id));
            return HostMode.Page(UsersPage(token, $"{user.Username} is deleted.", done: true,
                unsaved.Count == 0 ? null : $"Amberline could not save every change just now: {string.Join(", ", unsaved)}."));
        }));

        // A row's action: the antiforgery check and the user its form names, then the action, or the page that says why there is none.
        async Task<IResult> ActAsync(HttpContext context, IAntiforgery antiforgery, Func<PlatformAccount, string, IResult> act)
        {
            var form = await FormAsync(context, antiforgery);
            var token = Program.RegistrationToken(context, antiforgery);
            if (form is null) return HostMode.Page(UsersPage(token, Expired), StatusCodes.Status400BadRequest);
            var (user, refused) = Chosen(form["accountId"]);
            return user is null ? refused! : act(user, token);
        }

        // The user an action names. The host's own login is refused; an id that is no user's (deleted in another tab, or never one) is a
        // plain 404 page, never a failure.
        (PlatformAccount? User, IResult? Refused) Chosen(string? id)
        {
            if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var accountId)) return (null, Gone);
            if (accountId == HostAccount()) return (null, OwnLogin);
            return accounts.Find(accountId) is { } user ? (user, null) : (null, Gone);
        }

        // The sessions end first, in memory: a token file that cannot be written must never leave the user logged in. The token is then
        // revoked on a step of its own, as Delete's tidy-up does: a write that fails is logged and said on the page (the warning), never a
        // failed action, and the token is gone from memory all the same; after a restart the tunnel still refuses a disabled login's token
        // (AirRemoteAccess.Allowed).
        string? LogOut(long id)
        {
            sessions.EndAccount(id);
            try { remoteAccess.Revoke(id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Amberline could not save a user's remote sign-in: {ex.Message}");
                return "Amberline could not save every change just now: their remote sign-in.";
            }
            return null;
        }

        // Owner, 4 November 2026: Disable also clears the user's Cheat commands tick. It runs after every way in has ended, so a settings file
        // that cannot be read (the page then says so) never leaves a session open; Enable clears a tick such a failure left, so a returning
        // user never has cheats the host did not tick again; Delete clears it as the login goes.
        void ClearCheats(long id) => HostMode.Change(loop.Paths.Settings, saved => saved.CheatLogins().Contains(id)
            ? saved with { CheatAccounts = [.. saved.CheatLogins().Where(other => other != id)], AllowCheats = false } : saved);

        // unsaved: a warning under the notice (a delete whose tidy-up could not save every file).
        string UsersPage(string token, string? note = null, bool done = false, string? unsaved = null)
        {
            var host = HostAccount();
            var users = accounts.ListAccounts().Where(account => account.Id != host)
                .OrderBy(account => account.Username, StringComparer.Ordinal).ToArray();
            var online = sessions.Online().Select(session => session.AccountId).ToHashSet();
            var playing = games?.Playing() ?? [];
            var now = DateTimeOffset.UtcNow;
            // In a match (with its time since it launched), in a lobby or a queue, online, the last login or logout (its file keeps it to the hour,
            // so the first hour is "less than an hour ago"), or never.
            (string Text, bool Live) Status(PlatformAccount account) =>
                playing.TryGetValue(account.Id, out var since) ? (since is { } start ? "In game · " + Elapsed(now - start) : "In lobby", true)
                : online.Contains(account.Id) ? ("Online", true)
                : accounts.LastSeen(account.Id) is { } seen ? ("Last seen " + (now - seen < TimeSpan.FromHours(1) ? "less than an hour ago" : HostDashboard.Ago(now - seen)), false)
                : ("Never seen here", false);
            // Each button names its user for a screen reader; the visible text stays short. Batch 652: a new password says it is being made.
            string Post(PlatformAccount account, string action, string label, string named, string wait = "") =>
                $"<form method=post action={UsersPath}/{action}{wait}>{token}<input type=hidden name=accountId value={account.Id}>" +
                $"<button type=submit class=\"secondary small\">{label}<span class=vh> {named}</span></button></form>";
            string Row(PlatformAccount account)
            {
                var name = WebUtility.HtmlEncode(account.Username);
                var (status, live) = Status(account);
                var making = HostPages.Wait("Making a new password.");
                // A disabled user has Enable instead of New password and Disable: a new password is what lets them in again.
                var actions = (account.AccessRemovedAt is null
                        ? Post(account, "password", "New password", "for " + name, making) + Post(account, "disable", "Disable", name)
                        : Post(account, "enable", "Enable", name, making)) +
                    $"<a class=\"button secondary small\" href=\"{UsersPath}/delete?id={account.Id}\">Delete<span class=vh> {name}</span></a>";
                return $"<tr><td><b>{name}</b>" +
                    (account.Summoner.Equals(account.Username, StringComparison.OrdinalIgnoreCase) ? "" : $" <span class=tip>{WebUtility.HtmlEncode(account.Summoner)}</span>") +
                    (account.SignedUp ? " <span class=mark>Signed up</span>" : "") + (account.AccessRemovedAt is null ? "" : " <span class=mark>Disabled</span>") +
                    $"</td><td class={(live ? "\"words ok\"" : "words")}>{status}</td><td><div class=buttons>{actions}</div></td></tr>";
            }
            var shown = (note is null ? "" : done ? $"<p class=notice role=status>{WebUtility.HtmlEncode(note)}</p>" : HostPages.Errors((note, null))) +
                (unsaved is null ? "" : $"<p class=warn>{WebUtility.HtmlEncode(unsaved)}</p>");
            // Copy sign-up link, while sign-ups are on and the address is known (the script shows it: it copies the hidden link's text).
            var address = publicHost();
            var copy = signUp?.Url(address) is { } link
                ? "<button type=button class=secondary data-copy=signup-link data-needs-script hidden>Copy sign-up link</button>" +
                  $"<span id=signup-link class=vh>{WebUtility.HtmlEncode(link)}</span><span id=copy-status role=status class=tip></span>"
                : "";
            // Get the Play file, once the address is known and Amberline's template is there: else the link would download an error page.
            var play = address is not null && File.Exists(Template(loop))
                ? $"<a class=\"button secondary\" href={PlayFilePath} download>Get the Play file</a>" : "";
            // The search box filters the rows in the page (page.js); without the script it stays hidden and the table is simply complete.
            var list = users.Length == 0 ? "<p class=empty>No users yet. Add one or turn on sign-ups.</p>"
                : "<p class=table-search data-needs-script hidden><label for=user-search>Search users</label><input type=search id=user-search autocomplete=off></p>" +
                  "<table><thead><tr><th scope=col>User</th><th scope=col>Status</th><th scope=col>Actions</th></tr></thead>" +
                  $"<tbody id=users>{string.Concat(users.Select(Row))}</tbody></table><p id=users-none class=empty hidden>No users match your search.</p>" +
                  "<p class=tip>New password and Disable log the user out at once and keep their games. Delete cannot be undone.</p>";
            return $"<h1>Users ({users.Length})</h1>{shown}<div class=\"buttons user-actions\"><a class=button href={AddPath}>Add a user</a>{copy}{play}</div>{list}";
        }
    }

    /// <summary>303 See Other: the browser asks <paramref name="path"/> with a GET (Results has no such helper).</summary>
    private static IResult SeeOther(HttpContext context, string path)
    {
        context.Response.Headers.Location = path;
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    /// <summary>An action on a user who no longer exists (deleted in another tab, or a page open from before).</summary>
    private static readonly IResult Gone = HostMode.Page(
        $"<h1>This user no longer exists.</h1><p><a href={UsersPath}>Back to Users</a></p>", StatusCodes.Status404NotFound);

    /// <summary>The host's own login is never changed or deleted on Users: the host plays with it (Advanced sets its password).</summary>
    private static readonly IResult OwnLogin = HostMode.Page(
        "<h1>This is your own login</h1><p>Amberline keeps it: you play with it. Advanced can set its password.</p>" +
        $"<p><a href={UsersPath}>Back to Users</a></p>", StatusCodes.Status400BadRequest);

    /// <summary>The whole seconds of a match so far as mm:ss, as the dashboard's game cards show them.</summary>
    private static string Elapsed(TimeSpan time)
    {
        var seconds = (int)Math.Clamp(time.TotalSeconds, 0, int.MaxValue);
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60:00}:{seconds % 60:00}");
    }

    /// <summary>Delete's confirm page: the user, that it cannot be undone, and what it frees and keeps. A GET deletes nothing.</summary>
    private static string DeleteConfirm(string token, PlatformAccount user)
    {
        var name = WebUtility.HtmlEncode(user.Username);
        return $"<h1>Delete {name}?</h1><p>This cannot be undone. The login goes for good, and its username and summoner name " +
            $"({WebUtility.HtmlEncode(user.Summoner)}) can be taken again. Its past games stay, under that name.</p>" +
            $"<form method=post action={UsersPath}/delete>{token}<input type=hidden name=accountId value={user.Id}><div class=buttons>" +
            $"<button type=submit>Delete user</button> <a class=\"button secondary\" href={UsersPath}>Cancel</a></div></form>";
    }

    private static string Template(HostLoop loop) => Path.Combine(loop.Paths.App, TemplateFile);

    /// <summary>
    /// The server's Play file (Invite's download, and batch 656's sign-up page), or null when its template is missing. CRLF and ASCII, as
    /// tools/player-file.ps1 writes Play.cmd: cmd.exe reads the file.
    /// </summary>
    internal static IResult? PlayFile(HostLoop loop, string host, int remotePort, string certificateSha256, string name)
    {
        var template = Template(loop);
        if (!File.Exists(template)) return null;
        var text = PlatformBootstrap.RenderLauncher(File.ReadAllText(template).ReplaceLineEndings("\r\n"), host, remotePort, certificateSha256, name);
        return Results.File(Encoding.ASCII.GetBytes(text), "application/octet-stream", PlayFileName);
    }

    private static IResult AddressFirst() => HostMode.Page(
        $"<h1>Your address first</h1><p>The Play file needs your address, which Amberline does not know yet.</p>" +
        $"<p><a class=button{HostSteps.AddressWait} href={HostSteps.Start}>Give your address</a></p>", StatusCodes.Status409Conflict);

    private static async Task<IFormCollection?> FormAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return await context.Request.ReadFormAsync(context.RequestAborted);
        }
        catch (AntiforgeryValidationException) { return null; }
    }

    private static string HomeNote(bool homeOnly) => homeOnly ? $"<p class=warn>{HomeNetworkNote}</p>" : "";

    /// <param name="link">Batch 656: the sign-up link while Settings' switch is on, else empty.</param>
    private static string AddForm(string token, IReadOnlyList<string> problems, string username, string summoner, string mode, bool homeOnly, string link)
    {
        (string? Text, string? Field)[] shown = [.. problems.Select(HostPages.LoginProblem)];
        string? At(string id) => shown.FirstOrDefault(problem => problem.Field == id).Text;
        return $$"""
            <h1>Add a user</h1>
            <p>Make their login, then send them the Play file and a message.</p>
            {{HomeNote(homeOnly)}}
            {{HostPages.Errors(shown)}}
            <form method=post action={{AddPath}} novalidate>{{token}}
            {{HostPages.Field("username", "Their username", "3 to 24 letters (a to z), digits or _.", At("username"),
                $"required minlength=3 maxlength=24 autocomplete=off value=\"{WebUtility.HtmlEncode(username)}\"")}}
            {{HostPages.Field("summoner", "Summoner name", "What other players see. Empty: the username.", At("summoner"),
                $"maxlength={Amberline.Server.MatchRoster.MaxNameLength} autocomplete=off value=\"{WebUtility.HtmlEncode(summoner)}\"", required: false)}}
            {{HostSteps.StartChoice("Start them at", mode, At("mode"))}}
            {{HostPages.Field("password", "Password", "Empty: Amberline makes one. Or at least 10 characters.", At("password"),
                "type=password maxlength=128 autocomplete=new-password", required: false)}}
            <div class=actions><button type=submit>Create their login</button></div></form>
            {{link}}
            """;
    }

    /// <summary>
    /// The message box and its Copy button (<c>page.js</c> copies the box's text, and says "Copied" in the <c>copy-status</c> live
    /// region).
    /// </summary>
    private static string Message(string text) =>
        $"<pre id=message class=message>{WebUtility.HtmlEncode(text)}</pre>" +
        "<p class=buttons><button type=button data-copy=message>Copy</button> <span id=copy-status role=status></span></p>";

    private static string Ready(string username, string password, string name, bool homeOnly)
    {
        var user = WebUtility.HtmlEncode(username);
        var server = WebUtility.HtmlEncode(name);
        var message = Message($"""
            Join my game server "{name}"!
            You need your own 2011 game and AIR client.{(homeOnly ? "\nYour PC must be on the same home network as mine." : "")}
            Double-click the file I sent; it puts "{name}" on your desktop. Then log in with:
              username: {username}
              password: {password}
            """);
        return $"""
            <h1>{user} is ready</h1>
            {HomeNote(homeOnly)}
            <ol class=send>
            <li><p>Send them the Play file: <a href={PlayFilePath} download>Download the Play file for {server}</a></p></li>
            <li><p>Send them this message:</p>
            {message}</li>
            </ol>
            <p class=warn>The password is shown only now. <a href={UsersPath}>Users</a> can make a new one.</p>
            <p><a class="button secondary" href={AddPath}>Add another user</a></p>
            """;
    }

    /// <param name="again">The user had been disabled (Enable): the same page in those words.</param>
    /// <param name="unsaved">Final fix 2: a change the action could not save just now (the remote sign-in's file), said under the message.</param>
    private static string NewPasswordPage(string username, string password, string name, bool again, string? unsaved = null)
    {
        var user = WebUtility.HtmlEncode(username);
        var message = Message($"""
            Your {(again ? "access to" : "new password for")} "{name}"{(again ? " is back" : "")}:
              username: {username}
              password: {password}
            Start "{name}" from your desktop and log in with it.
            """);
        return $"""
            <h1>{(again ? $"{user} can log in again" : $"A new password for {user}")}</h1>
            <p>{(again ? "" : "Their old password no longer works. ")}Send them this message:</p>
            {message}
            <p class=warn>The password is shown only now.</p>{(unsaved is null ? "" : $"\n<p class=warn>{WebUtility.HtmlEncode(unsaved)}</p>")}
            <p><a class="button secondary" href={UsersPath}>Back</a></p>
            """;
    }
}
