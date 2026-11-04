using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: Invite a friend and Friends (Amberline Host design, section 2.4; retail players signed up on Riot's website and
/// downloaded Riot's installer). Under <c>/host</c>, behind <see cref="HostGate"/>. Invite makes the friend's login with
/// <see cref="PlatformAccounts.RegisterPublic"/> and a generated password shown once (never logged, never stored in plain text),
/// and hands out the one Play file, rendered from the template beside the Platform (<c>tools/play-bootstrap.cmd</c>) with the
/// server's name and address. Friends lists every login but the host's own (<c>hostAccount</c>): New password and Remove access
/// end the friend's sign-in token and sessions; Remove access sets a password nobody knows and deletes nothing
/// (<see cref="PlatformAccounts.RemoveAccess"/>: the row then says "Access removed" and offers Give access again, a new password).
/// </summary>
public static class HostInvite
{
    public const string InvitePath = "/host/invite", FriendsPath = "/host/friends", PlayFilePath = "/host/play-file";
    /// <summary>The Play-file template's name beside the Platform (<c>tools/build-server.ps1 -Layout Home</c> puts it there).</summary>
    public const string TemplateFile = "play-bootstrap.cmd";
    /// <summary>The downloaded file's name, as batch 619 names it.</summary>
    public const string PlayFileName = "Play.cmd";
    /// <summary>What the dashboard and Invite say while the host chose Only people on my home network (<see cref="HostAddress.Lan"/>).</summary>
    public const string HomeNetworkNote = "Friends must be on the same home network as this PC.";
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
    /// The host's pages for friends. <paramref name="publicHost"/> and <paramref name="serverName"/> are read on each request
    /// (<see cref="HostSteps"/> changes them without a restart); the Play file pins <paramref name="certificateSha256"/> on
    /// <paramref name="remotePort"/>, as <c>/bootstrap/client.json</c> does. Batch 656: while <paramref name="signUp"/>'s switch is on,
    /// Invite also shows its link.
    /// </summary>
    public static void Map(WebApplication app, HostLoop loop, Func<string?> publicHost, Func<string?> serverName, PlatformAccounts accounts,
        PlatformSessions sessions, AirRemoteAccess remoteAccess, int remotePort, string certificateSha256, HostSelfRegistration? signUp = null)
    {
        string Name() => serverName() ?? DefaultName;
        // Batch 657: a failed read must not drop the host's id, or Friends would list the host's own login and let a button change it.
        long? HostAccount() => HostMode.ReadStrict(loop.Paths.Settings).HostAccount;
        bool HomeOnly() => HostMode.ReadOrDefault(loop.Paths.Settings).AddressSource == HostAddress.Lan;
        string Link() => signUp?.Link(publicHost()) is { Length: > 0 } link
            ? $"<h2>Or send this link</h2><p>Anyone with it can make their own login.</p>{link}" : "";
        app.MapGet(InvitePath, (HttpContext context, IAntiforgery antiforgery) => publicHost() is null ? AddressFirst()
            : HostMode.Page(InviteForm(Program.RegistrationToken(context, antiforgery), [], "", "", "maxed", HomeOnly(), Link())));
        app.MapPost(InvitePath, async (HttpContext context, IAntiforgery antiforgery) =>
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
                    return HostMode.Page(Ready(account.Username, password, Name(), HomeOnly()));
                }
                catch (ArgumentException ex) { problems = [ex.Message]; }
            }
            return HostMode.Page(InviteForm(Program.RegistrationToken(context, antiforgery), problems, username, summoner, mode, HomeOnly(), Link()),
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
        app.MapGet(FriendsPath, (HttpContext context, IAntiforgery antiforgery) =>
            HostMode.Page(FriendsPage(Program.RegistrationToken(context, antiforgery), null)));
        app.MapPost(FriendsPath + "/password", (HttpContext context, IAntiforgery antiforgery) => ChangeAsync(context, antiforgery, remove: false));
        app.MapPost(FriendsPath + "/remove", (HttpContext context, IAntiforgery antiforgery) => ChangeAsync(context, antiforgery, remove: true));

        async Task<IResult> ChangeAsync(HttpContext context, IAntiforgery antiforgery, bool remove)
        {
            var form = await FormAsync(context, antiforgery);
            var friend = form is not null && long.TryParse(form["accountId"], NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
                id != HostAccount() ? accounts.Find(id) : null;
            if (friend is null)
                return HostMode.Page(FriendsPage(Program.RegistrationToken(context, antiforgery),
                    form is null ? Expired : "Choose one of your friends."), StatusCodes.Status400BadRequest);
            // Remove access: a password nobody is told, kept on record, so no login works until a new password (Give access again);
            // the account and its games stay.
            var password = remove ? null : NewPassword();
            if (password is null) accounts.RemoveAccess(friend.Id);
            else accounts.SetPassword(friend.Id, password);
            remoteAccess.Revoke(friend.Id);
            sessions.EndAccount(friend.Id);
            // Owner, 4 November 2026: Remove access also clears the friend's Cheat commands tick. It runs after every way in has ended,
            // so a settings file that cannot be read (the page then says so) never leaves a session open; Give access again clears a
            // tick such a failure left, so a returning friend never has cheats the host did not tick again.
            if (remove || friend.AccessRemovedAt is not null)
                HostMode.Change(loop.Paths.Settings, saved => saved.CheatLogins().Contains(friend.Id)
                    ? saved with { CheatAccounts = [.. saved.CheatLogins().Where(id => id != friend.Id)], AllowCheats = false } : saved);
            return password is null
                ? HostMode.Page(FriendsPage(Program.RegistrationToken(context, antiforgery),
                    $"{friend.Username} can no longer log in, and was logged out. Their games stay.", done: true))
                : HostMode.Page(NewPasswordPage(friend.Username, password, Name(), again: friend.AccessRemovedAt is not null));
        }

        string FriendsPage(string token, string? note, bool done = false)
        {
            var host = HostAccount();
            var online = sessions.Online().Select(session => session.AccountId).ToHashSet();
            var friends = accounts.ListAccounts().Where(account => account.Id != host)
                .OrderBy(account => account.Username, StringComparer.Ordinal).ToArray();
            string Seen(PlatformAccount account)
            {
                if (account.AccessRemovedAt is { } removed) return "<b>Access removed</b> on " + removed.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
                if (online.Contains(account.Id)) return "online now";
                var last = accounts.ResultsFor(account.Id).Select(result => result.CompletedAt).DefaultIfEmpty().Max();
                return last == default ? "no game yet" : "last game " + last.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
            }
            // Each button names its friend for a screen reader; the visible text stays short.
            // Batch 652: a new password (New password, Give access again) says it is being made.
            string Action(PlatformAccount account, string action, string label) =>
                $"<form method=post action={FriendsPath}/{action}{(action == "password" ? HostPages.Wait("Making a new password.") : "")}>{token}<input type=hidden name=accountId value={account.Id}>" +
                $"<button type=submit class=secondary>{label}<span class=vh> for {WebUtility.HtmlEncode(account.Username)}</span></button></form>";
            // A removed friend has one action: Give access again (a new password, shown once, as New password does).
            var rows = string.Concat(friends.Select(account =>
                $"<tr><td><b>{WebUtility.HtmlEncode(account.Username)}</b></td><td>{WebUtility.HtmlEncode(account.Summoner)}</td>" +
                $"<td class=words>{Seen(account)}</td><td><div class=buttons>" + (account.AccessRemovedAt is null
                    ? Action(account, "password", "New password") + Action(account, "remove", "Remove access")
                    : Action(account, "password", "Give access again")) + "</div></td></tr>"));
            var shown = note is null ? "" : done ? $"<p class=notice role=status>{WebUtility.HtmlEncode(note)}</p>" : HostPages.Errors((note, null));
            var list = friends.Length == 0 ? "<p>You have not invited anyone yet.</p>"
                : "<table><thead><tr><th scope=col>Username</th><th scope=col>Summoner name</th><th scope=col>Last seen</th><th scope=col>Change</th></tr></thead>" +
                  $"<tbody>{rows}</tbody></table>";
            return $$"""
                <h1>Friends ({{friends.Length}})</h1>
                {{shown}}
                <p><a class=button href={{InvitePath}}>Invite a friend</a></p>
                {{list}}
                <p class=tip>New password and Remove access log the friend out at once. Neither deletes their games.</p>
                """;
        }
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
    private static string InviteForm(string token, IReadOnlyList<string> problems, string username, string summoner, string mode, bool homeOnly, string link)
    {
        (string? Text, string? Field)[] shown = [.. problems.Select(HostPages.LoginProblem)];
        string? At(string id) => shown.FirstOrDefault(problem => problem.Field == id).Text;
        return $$"""
            <h1>Invite a friend</h1>
            <p>Make your friend a login, then send them the Play file and a message.</p>
            {{HomeNote(homeOnly)}}
            {{HostPages.Errors(shown)}}
            <form method=post action={{InvitePath}} novalidate>{{token}}
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
            <p class=warn>The password is shown only now. <a href={FriendsPath}>Friends</a> can make a new one.</p>
            <p><a class="button secondary" href={InvitePath}>Invite another friend</a></p>
            """;
    }

    /// <param name="again">The friend's access had been removed (Give access again): the same page in those words.</param>
    private static string NewPasswordPage(string username, string password, string name, bool again)
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
            <p class=warn>The password is shown only now.</p>
            <p><a class="button secondary" href={FriendsPath}>Back</a></p>
            """;
    }
}
