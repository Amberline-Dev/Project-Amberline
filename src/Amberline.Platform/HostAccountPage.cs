using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: My account (batch 751; the owner, 12 November 2026: users change their own password and delete their own account, "GDPR").
/// Retail players managed their login on Riot's website; a server hosted at home has none. Amberline's own start (<c>--home</c>) only, gated
/// as <c>/register</c> is on each address: on the local port a host path behind <see cref="HostGate"/> (the signed-in host's browser), on
/// the friends' port over HTTPS (<see cref="Serves"/>), whether or not sign-ups are on. Two forms, each one POST that carries the name and
/// the password; no web session but the antiforgery token.
/// <list type="bullet">
/// <item>New password: the sign-up page's password rule, then <see cref="PlatformAccounts.ChangeOwnPassword"/> and the Users page's
/// log-out (<see cref="UserRemoval.LogOut"/>: the client's sessions and the remote sign-in end). Allowed at any time, a running game goes on.</item>
/// <item>Delete my account: a ticked box, then the Users page's own Delete (<see cref="UserRemoval.Delete"/>): refused while the user is in a
/// lobby or a game, and for the host's own login (<see cref="PlatformSettings.HostAccount"/>), which may still change its password here.</item>
/// <item>A wrong name or password, an unknown name and a disabled login are one message, after the sign-in's own fixed-time check
/// (<see cref="PlatformAccounts.Authenticate"/>); the host's own login is refused only after it, so the page never says whose name it is.</item>
/// <item>Every POST counts toward the <see cref="Policy"/> limit, <see cref="PerAddress"/> an hour from one address (Program), and toward
/// <see cref="PerHour"/> from every address together; either refusal is <see cref="TooMany"/>'s plain page.</item>
/// <item>Nothing here writes a log line: no password, no username. A password is only hashed and never shown back.</item>
/// </list>
/// </summary>
public sealed class HostAccountPage(HostLoop loop, PlatformAccounts accounts, PlatformSessions sessions, AirRemoteAccess remoteAccess,
    PlatformGames? games, AirXmppHost? chat, int remotePort) : IDisposable
{
    public const string PagePath = "/account", StylePath = "/account/style.css";
    /// <summary>The per-address rate-limit policy (Program), apart from the sign-ups' <c>registration</c>.</summary>
    public const string Policy = "account";
    /// <summary>POSTs an hour from one address: enough to fix a typo, too few to guess a password.</summary>
    public const int PerAddress = 10;
    /// <summary>POSTs an hour from every address together, so many addresses cannot guess either.</summary>
    public const int PerHour = 60;

    /// <summary>A refused POST (either cap), in plain words.</summary>
    internal static readonly IResult TooMany = Shown("<h1>Too many tries</h1><p>Try again in an hour.</p>", StatusCodes.Status429TooManyRequests);

    private const string NoMatch = "That name and password do not match.";

    // ponytail: in memory, so a restart starts a new hour (the sign-up's cap does the same); keep the count in the data folder if restarts become a way around it.
    private readonly FixedWindowRateLimiter _all = new(new FixedWindowRateLimiterOptions
        { PermitLimit = PerHour, Window = TimeSpan.FromHours(1), QueueLimit = 0 });
    private readonly UserRemoval _removal = new(loop, accounts, games, sessions, remoteAccess, chat);

    /// <summary>A request the friends' port serves as My account: on that port, over HTTPS, the page or its stylesheet.</summary>
    public bool Serves(HttpContext context) => context.Connection.LocalPort == remotePort && context.Request.IsHttps &&
        (context.Request.Path == PagePath || context.Request.Path == StylePath);

    /// <summary>The page, its POST (held to <see cref="Policy"/>) and its stylesheet; the gates in front of them are Program's and HostGate's.</summary>
    public void Map(WebApplication app)
    {
        app.MapGet(PagePath, (HttpContext context, IAntiforgery antiforgery) => Shown(FormPage(Program.RegistrationToken(context, antiforgery))));
        app.MapPost(PagePath, (HttpContext context, IAntiforgery antiforgery) => PostAsync(context, antiforgery)).RequireRateLimiting(Policy);
        app.MapGet(StylePath, (HttpContext context) => HostPages.PublicStylesheet(context));
    }

    public void Dispose() => _all.Dispose();

    private async Task<IResult> PostAsync(HttpContext context, IAntiforgery antiforgery)
    {
        // Every POST counts, a failed one too.
        using var lease = _all.AttemptAcquire();
        if (!lease.IsAcquired) return TooMany;
        var form = await HostSteps.FormAsync(context, antiforgery);
        var token = Program.RegistrationToken(context, antiforgery);
        if (form is null) return Shown(FormPage(token, problems: (HostSteps.Expired, null)), StatusCodes.Status400BadRequest);
        var username = form["username"].ToString();
        var password = form["password"].ToString();
        return form["action"].ToString() == "delete"
            ? Delete(token, username, password, form["confirm"].ToString() == "yes")
            : Change(token, username, password, form["new"].ToString(), form["again"].ToString());
    }

    private IResult Change(string token, string username, string password, string next, string again)
    {
        // The form's own problems first, which need no password check; then the check, the same for every name.
        var rule = PlatformAccounts.Refusals(username, next, username).Password;
        if (rule is not null || next != again)
            return Shown(FormPage(token, "password", username, rule is null ? ("Type the same new password twice.", "pw-again") : (HostPages.LoginProblem(rule).Text, "pw-new")),
                StatusCodes.Status400BadRequest);
        // A host Disable between the password check and the change wins (ChangeOwnPassword refuses it under the store's lock): the same message.
        if (accounts.Authenticate(username, password) is not { } user || !accounts.ChangeOwnPassword(user.Id, next))
            return Shown(FormPage(token, "password", username, (NoMatch, "pw-username")), StatusCodes.Status400BadRequest);
        // A revoke that could not be saved is gone from memory only: after a restart the old token would work again, so the page says so (as the
        // host's New password does) and a second change runs the revoke again. Public sheet: .problem (the host sheet's .warn is not in it).
        var signedOut = _removal.LogOut(user.Id);
        return Shown("<h1>Password changed</h1><p class=notice role=status>Your password is changed. Sign in again in the client.</p>" + (signedOut ? ""
            : "<p class=problem role=alert>Amberline could not finish signing you out everywhere just now. Change your password again in a minute.</p>"));
    }

    private IResult Delete(string token, string username, string password, bool confirmed)
    {
        if (!confirmed)
            return Shown(FormPage(token, "delete", username, ("Tick the box to confirm that you want to delete your account.", "del-confirm")),
                StatusCodes.Status400BadRequest);
        if (accounts.Authenticate(username, password) is not { } user)
            return Shown(FormPage(token, "delete", username, (NoMatch, "del-username")), StatusCodes.Status400BadRequest);
        try
        {
            // Batch 657: a failed read must not drop the host's id, or the host's own login could go here.
            if (user.Id == HostMode.ReadStrict(loop.Paths.Settings).HostAccount)
                return Shown(FormPage(token, "delete", username, ("This is the host's own login. It cannot be deleted here.", null)), StatusCodes.Status400BadRequest);
        }
        catch (SettingsUnreadableException)
        {
            return Shown(FormPage(token, "delete", username, ("Amberline cannot delete it just now. Try again in a minute.", null)), StatusCodes.Status503ServiceUnavailable);
        }
        return _removal.Delete(user, $"{user.Username} deleted their own account").Outcome switch
        {
            DeleteOutcome.Deleted => Shown("<h1>Account deleted</h1><p class=notice role=status>Your account is deleted. Your past games stay, under your old name.</p>"),
            // The host Delete's words, said to the user.
            DeleteOutcome.InGame => Shown(FormPage(token, "delete", username,
                ("You are in a lobby or a game. Try again once you have left the lobby or the game has ended.", null)), StatusCodes.Status409Conflict),
            // Deleted meanwhile (another tab): a name like any other that does not match.
            _ => Shown(FormPage(token, "delete", username, (NoMatch, "del-username")), StatusCodes.Status400BadRequest),
        };
    }

    private static IResult Shown(string content, int status = StatusCodes.Status200OK) => HostPages.PublicPage(content, status, HostPages.AccountStyleHref);

    /// <param name="form">The form that was sent (<c>password</c> or <c>delete</c>): its username comes back, never a password.</param>
    private static string FormPage(string token, string? form = null, string username = "", params (string? Text, string? Field)[] problems)
    {
        string? At(string id) => problems.FirstOrDefault(problem => problem.Field == id).Text;
        string Name(string which) => form == which ? WebUtility.HtmlEncode(username) : "";
        return $$"""
            <h1>My account</h1>
            {{HostPages.Errors(problems)}}
            <h2>New password</h2>
            <form method=post action={{PagePath}} novalidate>{{token}}<input type=hidden name=action value=password>
            {{HostPages.Field("pw-username", "Username", null, At("pw-username"),
                $"required maxlength=24 autocomplete=username value=\"{Name("password")}\"", name: "username")}}
            {{HostPages.Field("pw-password", "Current password", null, At("pw-password"),
                "type=password required maxlength=128 autocomplete=current-password", name: "password")}}
            {{HostPages.Field("pw-new", "New password", "At least 10 characters.", At("pw-new"),
                "type=password required minlength=10 maxlength=128 autocomplete=new-password", name: "new")}}
            {{HostPages.Field("pw-again", "New password again", null, At("pw-again"),
                "type=password required minlength=10 maxlength=128 autocomplete=new-password", name: "again")}}
            <div class=actions><button type=submit>Change password</button></div></form>
            <h2>Delete my account</h2>
            <p>Your login goes for good. Your past games stay, under your name.</p>
            <form method=post action={{PagePath}} novalidate>{{token}}<input type=hidden name=action value=delete>
            {{HostPages.Field("del-username", "Username", null, At("del-username"),
                $"required maxlength=24 autocomplete=username value=\"{Name("delete")}\"", name: "username")}}
            {{HostPages.Field("del-password", "Password", null, At("del-password"),
                "type=password required maxlength=128 autocomplete=current-password", name: "password")}}
            {{HostPages.FieldError("del-confirm", At("del-confirm"))}}<label class=choice><input type=checkbox id=del-confirm name=confirm value=yes required{{
                HostPages.Described("del-confirm", false, At("del-confirm"))}}>I understand this deletes my login and cannot be undone</label>
            <div class=actions><button type=submit>Delete my account</button></div></form>
            """;
    }
}
