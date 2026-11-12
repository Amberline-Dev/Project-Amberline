using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: people register themselves (batch 656; the owner, 3 November 2026: "allow outside people to register too, by going to
/// the host ip address + /register (toggleable in host settings)"). Retail players signed up on Riot's website; a server hosted at home
/// has no such site. While Settings' switch is on (<see cref="PlatformSettings.SelfRegistration"/>, off unless the host turns it on),
/// the friends' port (HTTPS with Amberline's own certificate, so the browser warns once) serves three paths more: <see cref="PagePath"/>
/// (the form and its POST, through <see cref="Program.MapRegistration"/>), <see cref="PlayFilePath"/> (Invite's Play file for this server)
/// and <see cref="HostPages.PublicStylePath"/>. Nothing else of the host reaches that port: <see cref="Serves"/> is the one rule the
/// port's allow-list (<see cref="Program.RunAsync"/>) and <see cref="HostGate"/> let through.
/// <list type="bullet">
/// <item>The form carries an antiforgery token.</item>
/// <item>Every POST counts toward the <c>registration</c> limit, 12 an hour per address, and every new login toward <see cref="PerHour"/>
/// in all, so a flood from many addresses cannot fill the store. A refusal is <see cref="TooMany"/>'s plain page.</item>
/// <item>Nothing here writes a log line: no password, no username. The password is only hashed (<see cref="PlatformAccounts.RegisterPublic"/>)
/// and never shown back.</item>
/// <item>The pages have the host pages' policy (<see cref="HostPages.Policy"/>), no script and no link to a host page.</item>
/// </list>
/// A new login is a user like any other: Users lists it (marked Signed up, batch 748), and Disable or Delete ends it.
/// </summary>
public sealed class HostSelfRegistration(HostLoop loop, PlatformAccounts accounts, int remotePort, string certificateSha256, bool on) : IDisposable
{
    public const string PagePath = "/register", PlayFilePath = "/register/Play.cmd";
    /// <summary>New logins an hour from the sign-up page, from every address together.</summary>
    public const int PerHour = 30;
    /// <summary>What Settings and Invite say beside the link (the owner's words).</summary>
    public const string Warning = "Their browser warns that the connection is not private. They choose Advanced, then Continue.";

    /// <summary>A refused sign-up (either cap), in plain words.</summary>
    internal static readonly IResult TooMany =
        HostPages.PublicPage("<h1>Too many sign-ups</h1><p>Try again in an hour.</p>", StatusCodes.Status429TooManyRequests);

    // ponytail: in memory, so a restart starts a new hour; keep the count in the data folder if restarts ever become a way around it.
    private readonly FixedWindowRateLimiter _logins = new(new FixedWindowRateLimiterOptions
        { PermitLimit = PerHour, Window = TimeSpan.FromHours(1), QueueLimit = 0 });
    private volatile bool _on = on;

    /// <summary>The friends' port: there <c>/register</c> is this page, never the operator's.</summary>
    public int Port => remotePort;
    public bool On => _on;

    /// <summary>A request the friends' port serves as the sign-up: on that port, over HTTPS, one of the three paths, while the switch is on.</summary>
    public bool Serves(HttpContext context) => _on && context.Connection.LocalPort == remotePort && context.Request.IsHttps &&
        (context.Request.Path == PagePath || context.Request.Path == PlayFilePath || context.Request.Path == HostPages.PublicStylePath);

    /// <summary>Settings' switch: saved in platform.json and served at once, without a restart.</summary>
    internal void Set(bool value)
    {
        // Batch 657's one way to save: a failed read writes nothing (and the page says so) instead of defaults over the other fields.
        HostMode.Change(loop.Paths.Settings, saved => saved with { SelfRegistration = value });
        _on = value;
    }

    /// <summary>
    /// The sign-up link while the switch is on and the address is known, else null: the one place it is made (batch 748), for Settings,
    /// Add a user, Users and the Dashboard, so all of them give the same link (the saved address, never the router's).
    /// </summary>
    internal string? Url(string? publicHost) => _on && !string.IsNullOrWhiteSpace(publicHost) ? $"https://{publicHost}:{remotePort}{PagePath}" : null;

    /// <summary>The link to send, with Copy and the line on the warning, as Settings and Invite show it while the switch is on; else empty.</summary>
    internal string Link(string? publicHost) => !_on ? ""
        : Url(publicHost) is not { } url ? "<p>The link appears here once Amberline knows your address.</p>"
        : $"<pre id=register-link class=message>{WebUtility.HtmlEncode(url)}</pre>" +
          "<p class=buttons><button type=button data-copy=register-link>Copy</button> <span id=copy-status role=status></span></p>" +
          $"<p class=tip>{Warning}</p>";

    /// <summary>The Play file and the stylesheet; the form is <c>/register</c>'s (<see cref="Program.MapRegistration"/>).</summary>
    public void Map(WebApplication app)
    {
        app.MapGet(PlayFilePath, (HttpContext context) => !Serves(context) ? Results.NotFound()
            : Saved() is { PublicHost: { } host } saved && HostInvite.PlayFile(loop, host, remotePort, certificateSha256, Name(saved)) is { } file ? file
            : HostPages.PublicPage("<h1>The Play file is not ready</h1><p>Ask the host for it.</p>", StatusCodes.Status404NotFound));
        app.MapGet(HostPages.PublicStylePath, (HttpContext context) => Serves(context) ? HostPages.PublicStylesheet(context) : Results.NotFound());
    }

    /// <summary>GET <c>/register</c> on the friends' port.</summary>
    internal IResult Form(HttpContext context, IAntiforgery antiforgery) => !Serves(context) ? Results.NotFound()
        : HostPages.PublicPage(FormPage(Program.RegistrationToken(context, antiforgery), [], "", "", "maxed"));

    /// <summary>POST <c>/register</c> on the friends' port: every problem at once (batch 650's list), else the login, within the caps.</summary>
    internal async Task<IResult> CreateAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!Serves(context)) return Results.NotFound();
        var form = await HostSteps.FormAsync(context, antiforgery);
        var username = form?["username"].ToString() ?? "";
        var summoner = form?["summoner"].ToString() ?? "";
        var password = form?["password"].ToString() ?? "";
        var mode = form?["mode"].ToString() ?? "maxed";
        // An empty summoner name is the username, as Invite stores it.
        var stored = summoner.Trim().Length == 0 ? username : summoner;
        List<string> problems = form is null ? [HostSteps.Expired] : Problems(username, password, stored, mode);
        if (problems.Count == 0)
        {
            using var lease = _logins.AttemptAcquire();
            if (!lease.IsAcquired) return TooMany;
            try
            {
                // Batch 748: marked as signed up, which the host's Users page shows.
                var name = accounts.RegisterPublic(username, password, stored, maxed: mode == "maxed", signedUp: true).Username;
                // NOT RETAIL: the host page's activity feed names a sign-up by its username only, never the password.
                loop.Activity.Add($"{name} signed up");
                return HostPages.PublicPage(Ready(name));
            }
            catch (ArgumentException ex) { problems = [ex.Message]; }
        }
        return HostPages.PublicPage(FormPage(Program.RegistrationToken(context, antiforgery), problems, username, summoner, mode),
            StatusCodes.Status400BadRequest);
    }

    public void Dispose() => _logins.Dispose();

    private PlatformSettings Saved() => HostMode.ReadOrDefault(loop.Paths.Settings);

    private static string Name(PlatformSettings saved) => saved.ServerName ?? HostInvite.DefaultName;

    /// <summary>The store's refusals of the three fields and a name already taken, then the start choice (the host's own login step's order).</summary>
    private List<string> Problems(string username, string password, string stored, string mode)
    {
        var (name, secret, nick) = PlatformAccounts.Refusals(username, password, stored);
        // An empty summoner field takes the username, whose own rule is the stricter one, so only its message shows.
        return [.. new[] { name, secret, stored == username ? null : nick, accounts.TakenRefusal(username, stored),
            mode is "maxed" or "fresh" ? null : "Choose where your login starts." }.OfType<string>()];
    }

    private string FormPage(string token, IReadOnlyList<string> problems, string username, string summoner, string mode)
    {
        (string? Text, string? Field)[] shown = [.. problems.Select(HostPages.LoginProblem)];
        string? At(string id) => shown.FirstOrDefault(problem => problem.Field == id).Text;
        return $$"""
            <h1>Create your login</h1>
            {{HostPages.Errors(shown)}}
            <form method=post action={{PagePath}} novalidate>{{token}}
            {{HostPages.Field("username", "Username", "3 to 24 letters (a to z), digits or _.", At("username"),
                $"required minlength=3 maxlength=24 autocomplete=username value=\"{WebUtility.HtmlEncode(username)}\"")}}
            {{HostPages.Field("summoner", "Summoner name", "What other players see. Empty: your username.", At("summoner"),
                $"maxlength={Amberline.Server.MatchRoster.MaxNameLength} value=\"{WebUtility.HtmlEncode(summoner)}\"", required: false)}}
            {{HostPages.Field("password", "Password", "At least 10 characters.", At("password"),
                "type=password required minlength=10 maxlength=128 autocomplete=new-password")}}
            {{HostSteps.StartChoice("Start at", mode, At("mode"))}}
            <div class=actions><button type=submit>Create your login</button></div></form>
            <p class=already>Already have a login?</p><p><a class=button href={{PlayFilePath}} download>Download the Play file</a></p>
            """;
    }

    private string Ready(string username) => $"""
        <h1>Your login is ready</h1>
        <p class=notice role=status>Log in as <b>{WebUtility.HtmlEncode(username)}</b> with the password you chose.</p>
        <p><a class=button href={PlayFilePath} download>Download the Play file</a></p>
        <p>Double-click the Play file: it puts "{WebUtility.HtmlEncode(Name(Saved()))}" on your desktop. Start it there and log in.</p>
        """;
}
