using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.AspNetCore.Antiforgery;

namespace Amberline.Platform;

/// <summary>
/// Where the address step finds the host's address. <see cref="Offline"/>, every loop's default, asks nobody: no router answers
/// and the lookup finds nothing, so a check never reaches the network. The real start uses <see cref="Live"/>. <see cref="Home"/>
/// is this PC's own address on the home network, which the home-network choice offers (null: none known).
/// </summary>
public sealed record HostAddressSources(Func<CancellationToken, Task<IPAddress?>> Router, Func<CancellationToken, Task<string?>> Lookup,
    Func<IPAddress?>? Home = null)
{
    public const string LookupService = "api.ipify.org";

    public static HostAddressSources Offline { get; } = new(_ => Task.FromResult<IPAddress?>(null), _ => Task.FromResult<string?>(null));

    /// <summary>
    /// The router's own answer through UPnP (no third party), the outside lookup, which only the host's press calls, and this PC's
    /// address on the interface towards the default gateway (the address the router sees; nothing is sent).
    /// </summary>
    public static HostAddressSources Live { get; } = new(RouterAsync, LookupAsync, () => HostNetwork.DefaultRoute().Computer);

    private static async Task<IPAddress?> RouterAsync(CancellationToken cancellationToken)
    {
        await using var router = await UpnpGateway.DiscoverAsync(cancellationToken: cancellationToken);
        return router is null ? null : await router.ExternalAddressAsync(cancellationToken);
    }

    private static async Task<string?> LookupAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1024 };
        return await http.GetStringAsync("https://" + LookupService + "/", cancellationToken);
    }
}

/// <summary>The rules for what the first-run steps save (Amberline Host design, section 2.2).</summary>
public static class HostAddress
{
    /// <summary>
    /// Where the saved address came from (<c>addressSource</c> in <c>platform.json</c>). <see cref="Lan"/> is the home-network
    /// choice: friends on the same network only, so the router is never asked.
    /// </summary>
    public const string Router = "router", Typed = "typed", Lookup = "lookup", Lan = "lan";

    // PlayerInstall.ServerName's rule (a desktop file name; tools/player-file.ps1 has it too): keep the three in step.
    private static readonly Regex NamePattern = new(@"^[A-Za-z0-9](?:[A-Za-z0-9 ._-]{0,38}[A-Za-z0-9])?\z", RegexOptions.CultureInvariant);
    private static readonly Regex ReservedName = new(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Null for a name friends' desktop icons can carry, else why not, in plain words.</summary>
    public static string? NameProblem(string name) => NamePattern.IsMatch(name) && !ReservedName.IsMatch(name) ? null
        : "A server name has 1 to 40 letters, digits, spaces, dots, - or _, and starts and ends with a letter or digit.";

    /// <summary>
    /// The address as saved (a name in lower case), or why friends could not use it. A name needs a dot and the DNS label rules;
    /// an IPv4 address must be public (<see cref="WanAddress.Classify"/>): a home-network, shared-provider or loopback address
    /// never reaches this PC from a friend's home. Whatever passes also passes the host rule of <see cref="PlatformBootstrap"/>.
    /// </summary>
    public static (string? Host, string? Problem) Check(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return (null, "Type your address.");
        if (IPAddress.TryParse(value, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetworkV6)
            return (null, "Amberline needs your IPv4 address (four numbers with dots) or a name for your address.");
        if (value.IndexOfAny([':', '/', '\\', ' ', '@', '?', '#']) >= 0)
            return (null, "Type only the address, for example 203.0.113.7 or yourname.duckdns.org, without http:// or anything after it.");
        if (value.Length > 253) return (null, "This address is too long.");
        if (value.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork || address.ToString() != value)
                return (null, "This is not a whole address: four numbers from 0 to 255 with dots, for example 203.0.113.7.");
            return WanAddress.Classify(address) switch
            {
                WanAddressClass.Public => (value, null),
                WanAddressClass.Private => (null, "This address belongs to your home network, so players outside cannot use it. " +
                    "Type the address the internet sees, or choose Only people on my home network."),
                WanAddressClass.Shared => (null, WanAddress.Problem(WanAddressClass.Shared)!.Message),
                _ => (null, "Other computers cannot reach this address. Type the address the internet sees."),
            };
        }
        value = value.ToLowerInvariant();
        var labels = value.Split('.');
        if (labels.Length < 2 || labels[^1].All(char.IsAsciiDigit) || labels.Any(label => label.Length is 0 or > 63 ||
                label[0] == '-' || label[^1] == '-' || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            return (null, "Amberline cannot use this name. Use letters a to z, digits and hyphens, with dots between parts, " +
                "for example yourname.duckdns.org.");
        return (value, null);
    }

    /// <summary>
    /// The home-network choice's address as saved, or why friends on the home network could not use it: a private IPv4 address
    /// (10/8, 172.16/12, 192.168/16, RFC 1918). Loopback, link-local, the provider's shared range (100.64/10), internet addresses
    /// and names are refused in plain words. Whatever passes also passes the host rule of <see cref="PlatformBootstrap"/>.
    /// </summary>
    public static (string? Host, string? Problem) CheckLan(string? text)
    {
        const string Example = "Type this PC's address on your home network, for example 192.168.1.20.";
        var value = (text ?? "").Trim();
        if (value.Length == 0) return (null, Example);
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork || address.ToString() != value)
            return (null, "This is not a home-network address: four numbers from 0 to 255 with dots, for example 192.168.1.20.");
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 127) return (null, "An address that starts with 127 means \"this PC itself\", so players cannot use it. " + Example);
        if (bytes[0] == 169 && bytes[1] == 254)
            return (null, "Windows picks an address that starts with 169.254 by itself when the router gave none. Check that this PC is connected to your router.");
        return WanAddress.Classify(address) switch
        {
            WanAddressClass.Private => (value, null),
            WanAddressClass.Shared => (null, "This address belongs to your internet provider, not your home network. " + Example),
            WanAddressClass.Public => (null, "This is an internet address, not a home-network one. For players outside your home network, choose another way above."),
            _ => (null, "Other computers cannot reach this address. " + Example),
        };
    }
}

/// <summary>
/// NOT RETAIL: the home host's first run (Amberline Host design, section 2.2; retail had no host). <c>/open</c> leads to
/// <see cref="Start"/>, which sends the host to the first step not done yet: the server's name, the game folders (setup mode's
/// <c>/setup</c>), then in the full Platform the host's own login and the address friends connect to, then the dashboard. Every
/// step shows what was saved and has Back, but the login (batch 646: the steps before it ran in setup mode, which has ended; the
/// page says Settings changes them). The answers go into <c>platform.json</c> (<c>serverName</c>, <c>hostAccount</c>,
/// <c>publicHost</c>, <c>addressSource</c>); the name and the address apply at once, without a restart that would end games:
/// <see cref="PublicHost"/> and <see cref="ServerName"/> are what <c>/bootstrap/client.json</c> serves. A password is hashed by
/// <see cref="PlatformAccounts.RegisterPublic"/> and never written, logged or shown again.
/// </summary>
public sealed class HostSteps
{
    public const string Start = "/host/setup";
    /// <summary>The name step, before the folder step (<see cref="HostFolders"/> links Back to it).</summary>
    public const string NamePath = "/host/setup/name";
    private const string LoginPath = "/host/setup/login", AddressPath = "/host/setup/address";
    private static readonly TimeSpan AskFor = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Where a step's form is shown: on the first run (its step number, Back to the step before, then on to the next step) or in
    /// Settings (batch 641: no number, the nav, Back to Settings, and Settings again with a note for friends after a save).
    /// </summary>
    private sealed record StepPlace(string Path, int Step, string? Back, string Done, string Submit);

    private static readonly StepPlace FirstName = new(NamePath, 1, null, Start, "Continue"),
        FirstAddress = new(AddressPath, 4, LoginPath, Start, "Continue"),
        SettingsName = new(HostSettingsPage.NamePath, 0, HostSettingsPage.PagePath, HostSettingsPage.PagePath + "?saved=name", "Save"),
        SettingsAddress = new(HostSettingsPage.AddressPath, 0, HostSettingsPage.PagePath, HostSettingsPage.PagePath + "?saved=address", "Save");
    private readonly HostLoop _loop;
    private readonly PlatformAccounts? _accounts;
    private readonly Lock _save = new();
    private volatile string? _publicHost, _serverName, _addressSource;

    private HostSteps(HostLoop loop, PlatformAccounts? accounts)
    {
        _loop = loop;
        _accounts = accounts;
        var saved = Saved();
        _publicHost = saved.PublicHost;
        _serverName = saved.ServerName;
        _addressSource = saved.AddressSource;
    }

    public string? PublicHost => _publicHost;
    public string? ServerName => _serverName;

    /// <summary>The host chose Only people on my home network (<see cref="HostAddress.Lan"/>): no router, no address watch.</summary>
    public bool HomeNetworkOnly => _addressSource == HostAddress.Lan;

    /// <summary>Called after a saved address turned the home-network choice on or off (the dashboard tests the network again).</summary>
    internal Action? HomeNetworkChanged { get; set; }

    /// <summary>The steps of the running mode: setup mode (no account store) has the name; the full Platform has every step.</summary>
    public static HostSteps Map(WebApplication app, HostLoop loop, PlatformAccounts? accounts)
    {
        var steps = new HostSteps(loop, accounts);
        app.MapGet(Start, () => Results.Redirect(steps.Next()));
        steps.MapName(app, FirstName);
        if (accounts is null)
        {
            // Batch 655: setup mode has no account store yet, so its later steps lead on to the folder step instead of a bare 404.
            app.MapMethods(LoginPath, [HttpMethods.Get, HttpMethods.Post], () => Results.Redirect(Start));
            app.MapMethods(AddressPath, [HttpMethods.Get, HttpMethods.Post], () => Results.Redirect(Start));
            return steps;
        }
        app.MapGet(LoginPath, (HttpContext context, IAntiforgery antiforgery) => steps.LoginPage(accounts, Program.RegistrationToken(context, antiforgery)));
        app.MapPost(LoginPath, (HttpContext context, IAntiforgery antiforgery) => steps.CreateLoginAsync(accounts, context, antiforgery));
        steps.MapAddress(app, FirstAddress);
        return steps;
    }

    /// <summary>Settings' name and address pages (<see cref="HostSettingsPage"/>): the same forms, checks and saves as the first run.</summary>
    public void MapSettings(WebApplication app)
    {
        MapName(app, SettingsName);
        MapAddress(app, SettingsAddress);
    }

    private void MapName(WebApplication app, StepPlace place)
    {
        app.MapGet(place.Path, (HttpContext context, IAntiforgery antiforgery) =>
            HostMode.Page(NameForm(Program.RegistrationToken(context, antiforgery), null, Saved().ServerName ?? "", place), step: place.Step));
        app.MapPost(place.Path, (HttpContext context, IAntiforgery antiforgery) => SaveNameAsync(context, antiforgery, place));
    }

    private void MapAddress(WebApplication app, StepPlace place)
    {
        app.MapGet(place.Path, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            var router = await AskRouterAsync(context.RequestAborted);
            var saved = Saved();
            var home = HomeAddress();
            return HostMode.Page(AddressForm(Program.RegistrationToken(context, antiforgery), null, router,
                saved.AddressSource is HostAddress.Typed or HostAddress.Lan ? saved.AddressSource : HostAddress.Router,
                saved.AddressSource == HostAddress.Typed ? saved.PublicHost ?? "" : "",
                saved.AddressSource == HostAddress.Lan ? saved.PublicHost ?? "" : home ?? "", saved, HostUpdates.Read(_loop.Paths.Data).Check ?? true,
                place), step: place.Step);
        });
        app.MapPost(place.Path, (HttpContext context, IAntiforgery antiforgery) => SaveAddressAsync(context, antiforgery, place));
    }

    private PlatformSettings Saved() => HostMode.ReadOrDefault(_loop.Paths.Settings);

    /// <summary>This PC's own home-network address for the home-network choice, or null when it has none (a private IPv4 address only).</summary>
    private string? HomeAddress() => _loop.Addresses.Home?.Invoke() is { } address ? HostAddress.CheckLan(address.ToString()).Host : null;

    private string Next()
    {
        var saved = Saved();
        if (string.IsNullOrWhiteSpace(saved.ServerName)) return NamePath;
        if (_accounts is null) return "/setup";
        if (saved.HostAccount is not long id || _accounts.Find(id) is null) return LoginPath;
        return string.IsNullOrWhiteSpace(saved.PublicHost) ? AddressPath : HostDashboard.PagePath;
    }

    private void Save(Func<PlatformSettings, PlatformSettings> change)
    {
        lock (_save) HostMode.Change(_loop.Paths.Settings, change);
    }

    /// <summary>
    /// The router reports a new address (<see cref="HostAttention"/>): saved and served at once, like the address step's, but only while
    /// the host's choice is still the router's address. True when it changed the saved address.
    /// </summary>
    internal bool Follow(string host)
    {
        lock (_save)
        {
            try
            {
                if (!HostMode.Change(_loop.Paths.Settings, saved =>
                        saved.AddressSource != HostAddress.Router || saved.PublicHost == host ? saved : saved with { PublicHost = host }))
                    return false;
            }
            // Not readable now: nothing was written, and the next period asks the router again.
            catch (SettingsUnreadableException) { return false; }
            _publicHost = host;
            return true;
        }
    }

    private async Task<IResult> SaveNameAsync(HttpContext context, IAntiforgery antiforgery, StepPlace place)
    {
        var form = await FormAsync(context, antiforgery);
        var name = form?["name"].ToString().Trim() ?? "";
        var problem = form is null ? Expired : HostAddress.NameProblem(name);
        if (problem is not null)
            return HostMode.Page(NameForm(Program.RegistrationToken(context, antiforgery), problem, name, place), StatusCodes.Status400BadRequest, step: place.Step);
        Save(saved => saved with { ServerName = name });
        _serverName = name;
        return Results.Redirect(place.Done);
    }

    private IResult LoginPage(PlatformAccounts accounts, string token)
    {
        if (Saved().HostAccount is not long id || accounts.Find(id) is not { } account) return HostMode.Page(LoginForm(token, [], "", "", "maxed"), step: 3);
        return HostMode.Page($"""
            <h1>Your own login</h1>
            <p>Your login <b>{WebUtility.HtmlEncode(account.Username)}</b> is ready.</p>
            <div class=actions><a class=button href={Start}{AddressWait}>Continue</a></div>
            """, step: 3);
    }

    private async Task<IResult> CreateLoginAsync(PlatformAccounts accounts, HttpContext context, IAntiforgery antiforgery)
    {
        if (Saved().HostAccount is long id && accounts.Find(id) is not null) return Results.Redirect(Start);
        var form = await FormAsync(context, antiforgery);
        var username = form?["username"].ToString() ?? "";
        var summoner = form?["summoner"].ToString() ?? "";
        var mode = form?["mode"].ToString() ?? "maxed";
        // Batch 649: every problem at once, each beside its own field: the store's refusals and a name already taken, then the start choice.
        List<string> problems = form is null ? [Expired] : LoginProblems(accounts, username, form["password"].ToString(), summoner, mode);
        if (problems.Count == 0)
        {
            try
            {
                var account = accounts.RegisterPublic(username, form!["password"].ToString(), summoner, maxed: mode == "maxed");
                Save(saved => saved with { HostAccount = account.Id });
                return Results.Redirect(Start);
            }
            catch (ArgumentException ex) { problems = [ex.Message]; }
        }
        return HostMode.Page(LoginForm(Program.RegistrationToken(context, antiforgery), problems, username, summoner, mode),
            StatusCodes.Status400BadRequest, step: 3);
    }

    /// <summary>
    /// Every refusal of a new login at once (batch 649), in the store's words: each field's (<see cref="PlatformAccounts.Refusals"/>), a
    /// name already taken, and the start choice. The password is checked here and never kept or shown again.
    /// </summary>
    private static List<string> LoginProblems(PlatformAccounts accounts, string username, string password, string summoner, string mode)
    {
        var (name, secret, nick) = PlatformAccounts.Refusals(username, password, summoner);
        return [.. new[] { name, secret, nick, accounts.TakenRefusal(username, summoner),
            mode is "maxed" or "fresh" ? null : "Choose where your login starts." }.OfType<string>()];
    }

    private async Task<IResult> SaveAddressAsync(HttpContext context, IAntiforgery antiforgery, StepPlace place)
    {
        var form = await FormAsync(context, antiforgery);
        var choice = form?["choice"].ToString() ?? "";
        var typed = form?["address"].ToString() ?? "";
        var lan = form?["lan"].ToString() ?? "";
        var updates = form is null ? HostUpdates.Read(_loop.Paths.Data).Check ?? true : form["updates"] == "on";
        var router = await AskRouterAsync(context.RequestAborted);
        string? host = null, problem;
        switch (choice)
        {
            case HostAddress.Router when form is not null:
                problem = router is null ? UpnpGateway.NotFound.Message : WanAddress.Problem(WanAddress.Classify(router))?.Message;
                if (problem is null) host = router!.ToString();
                break;
            case HostAddress.Typed when form is not null:
                (host, problem) = HostAddress.Check(typed);
                break;
            case HostAddress.Lan when form is not null:
                (host, problem) = HostAddress.CheckLan(lan);
                break;
            case HostAddress.Lookup when form is not null:
                // The host's press is the consent: the one request that tells an outside service this PC's address.
                var found = await AskLookupAsync(context.RequestAborted);
                problem = found is null
                    ? $"Amberline could not ask {HostAddressSources.LookupService}. Check that this PC is online, or type your address."
                    : !IPAddress.TryParse(found, out var address) || address.AddressFamily != AddressFamily.InterNetwork || address.ToString() != found
                        ? $"{HostAddressSources.LookupService} gave an answer Amberline cannot use. Type your address instead."
                        : null;
                if (problem is null) (host, problem) = HostAddress.Check(found);
                break;
            default:
                problem = form is null ? Expired : "Choose one of the ways to give your address.";
                break;
        }
        if (problem is not null)
            return HostMode.Page(AddressForm(Program.RegistrationToken(context, antiforgery), problem, router, choice, typed, lan,
                Saved(), updates, place), StatusCodes.Status400BadRequest, step: place.Step);
        Save(saved => saved with { PublicHost = host, AddressSource = choice });
        // The first run asks the update question on this step; Settings leaves it to the Updates page.
        if (place.Step > 0) HostUpdates.SaveChoice(_loop.Paths.Data, updates);
        _publicHost = host;
        var wasHomeOnly = HomeNetworkOnly;
        _addressSource = choice;
        if (wasHomeOnly != HomeNetworkOnly) HomeNetworkChanged?.Invoke();
        return Results.Redirect(place.Done);
    }

    private async Task<IPAddress?> AskRouterAsync(CancellationToken aborted)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        wait.CancelAfter(AskFor);
        try { return await _loop.Addresses.Router(wait.Token); }
        catch (Exception ex) when (ex is SocketException or HttpRequestException or IOException or OperationCanceledException or
                                       InvalidOperationException or XmlException)
        {
            // The router's own address stays out of the log, so only what failed is written.
            Console.WriteLine($"The router did not tell its address ({ex.GetType().Name}).");
            return null;
        }
    }

    private async Task<string?> AskLookupAsync(CancellationToken aborted)
    {
        try { return (await _loop.Addresses.Lookup(aborted))?.Trim(); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            Console.WriteLine($"The address lookup failed ({ex.GetType().Name}).");
            return null;
        }
    }

    internal const string Expired = "The page expired. Please try again.";

    /// <summary>The posted form, or null when its antiforgery token is missing or stale (the page expired).</summary>
    internal static async Task<IFormCollection?> FormAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return await context.Request.ReadFormAsync(context.RequestAborted);
        }
        catch (AntiforgeryValidationException) { return null; }
    }

    private static string Checked(bool on) => on ? " checked" : "";

    /// <summary>The problem when it belongs to field <paramref name="id"/>, for the message beside it.</summary>
    private static string? At((string? Text, string? Field) problem, string id) => problem.Field == id ? problem.Text : null;

    /// <summary>The first problem that belongs to field <paramref name="id"/>, for the message beside it (batch 649: a login's problems).</summary>
    private static string? At(IEnumerable<(string? Text, string? Field)> problems, string id) => problems.FirstOrDefault(problem => problem.Field == id).Text;

    /// <summary>
    /// A form's action row: Back when the place has one (Cancel in Settings, batch 652: leaving the form without saving), then the
    /// place's submit button.
    /// </summary>
    private static string Actions(StepPlace place) =>
        $"<div class=actions>{(place.Back is null ? "" : $"<a href={place.Back}>{(place.Step > 0 ? "Back" : "Cancel")}</a>")}<button type=submit>{place.Submit}</button></div>";

    /// <summary>The address page asks the router first (up to <see cref="AskFor"/>): what its links and its form say while they wait (batch 652).</summary>
    internal static readonly string AddressWait = HostPages.Wait("Asking your router for your address. This takes up to 10 seconds.");

    /// <summary>Where a saved address came from, as the address pages and Settings say it.</summary>
    internal static string Source(string? addressSource) => addressSource switch
    {
        HostAddress.Router => "from your router",
        HostAddress.Lookup => "found on the internet",
        HostAddress.Lan => "home network only",
        _ => "typed by you",
    };

    private const string Welcome = """
        <h1>Welcome to Amberline</h1>
        <p>Host the 2011 game for your players on this PC.</p>
        """;

    private const string NewName = """
        <h1>Change the server name</h1>
        <p>Users see the new name once you send them the new Play file.</p>
        """;

    private static string NameForm(string token, string? problem, string name, StepPlace place)
    {
        (string? Text, string? Field) shown = (problem, problem == Expired ? null : "name");
        return $$"""
            {{(place.Step > 0 ? Welcome : NewName)}}
            {{HostPages.Errors(shown)}}
            <form method=post action={{place.Path}} novalidate>{{token}}
            {{HostPages.Field("name", "Server name", "Shown on your users' desktop icon. Up to 40 letters, digits, spaces, dots, - or _, for example Friday night.",
                At(shown, "name"), $"required maxlength=40 value=\"{WebUtility.HtmlEncode(name)}\"")}}
            {{Actions(place)}}</form>
            """;
    }

    /// <summary>The start-level choice of a new login (the host's own here, a friend's on Invite).</summary>
    internal static string StartChoice(string legend, string mode, string? problem) => $"""
        <fieldset id=mode{(problem is null ? "" : " aria-describedby=mode-error")}><legend>{legend}</legend>{HostPages.FieldError("mode", problem)}
        <label class=choice><input type=radio name=mode value=maxed{Checked(mode != "fresh")}>Level 30, everything unlocked</label>
        <label class=choice><input type=radio name=mode value=fresh{Checked(mode == "fresh")}>Level 1, earn everything</label></fieldset>
        """;

    private static string LoginForm(string token, IReadOnlyList<string> problems, string username, string summoner, string mode)
    {
        var shown = problems.Select(HostPages.LoginProblem).Select<(string Text, string? Field), (string? Text, string? Field)>(problem => problem).ToArray();
        return $$"""
            <h1>Your own login</h1>
            <p>Choose your own login for the game client.</p>
            <p class=tip>Your name and folders are saved; Settings changes them later.</p>
            {{HostPages.Errors(shown)}}
            <form method=post action={{LoginPath}} novalidate>{{token}}
            {{HostPages.Field("username", "Username", "3 to 24 letters (a to z), digits or _.", At(shown, "username"),
                $"required minlength=3 maxlength=24 autocomplete=username value=\"{WebUtility.HtmlEncode(username)}\"")}}
            {{HostPages.Field("summoner", "Summoner name", "What other players see.", At(shown, "summoner"),
                $"required maxlength={Amberline.Server.MatchRoster.MaxNameLength} value=\"{WebUtility.HtmlEncode(summoner)}\"")}}
            {{HostPages.Field("password", "Password", "At least 10 characters.", At(shown, "password"),
                "type=password required minlength=10 maxlength=128 autocomplete=new-password")}}
            {{StartChoice("Start at", mode, At(shown, "mode"))}}
            <div class=actions><button type=submit>Continue</button></div></form>
            """;
    }

    /// <param name="lan">The home-network choice's field: what was saved or typed, else this PC's own address.</param>
    private static string AddressForm(string token, string? problem, IPAddress? router, string choice, string typed, string lan,
        PlatformSettings saved, bool updates, StepPlace place)
    {
        var kind = WanAddress.Classify(router);
        var card = router is null ? null : WanAddress.Problem(kind);
        var offerRouter = kind == WanAddressClass.Public;
        var from = Source(saved.AddressSource);
        (string? Text, string? Field) shown = (problem, problem == Expired ? null
            : choice switch { HostAddress.Typed => "address", HostAddress.Lan => "lan", _ => "choice" });
        // Batch 646: with the home-network choice the router's address does not matter, so its card is a tip there, not a problem.
        var routerCard = card is null ? ""
            : $"<p class={(choice == HostAddress.Lan ? "tip>Only for players outside your home network: " : "problem>")}" +
              $"{WebUtility.HtmlEncode(card.Message)} {WebUtility.HtmlEncode(card.WhatToDo)} ({card.Code})</p>";
        return $$"""
            <h1>{{(place.Step > 0 ? "Your address" : "Change your address")}}</h1>
            {{(string.IsNullOrWhiteSpace(saved.PublicHost) ? "" : $"<p class=saved>Saved now: <b>{WebUtility.HtmlEncode(saved.PublicHost)}</b> ({from}).</p>")}}
            {{HostPages.Errors(shown)}}
            {{routerCard}}
            <form method=post action={{place.Path}}{{HostPages.Wait("Checking your address. This takes up to 20 seconds.")}}>{{token}}
            <fieldset id=choice{{(At(shown, "choice") is null ? "" : " aria-describedby=choice-error")}}><legend>How do your players find this PC?</legend>
            {{HostPages.FieldError("choice", At(shown, "choice"))}}
            {{(offerRouter ? $"<label class=choice><input type=radio name=choice value=router{Checked(choice is not (HostAddress.Typed or HostAddress.Lookup or HostAddress.Lan))}>The address your router reports: {router}</label>" : "")}}
            <label class=choice><input type=radio name=choice value=typed{{Checked(choice == HostAddress.Typed || (!offerRouter && choice is not (HostAddress.Lookup or HostAddress.Lan)))}}>{{(offerRouter
                ? "A name for my address" : "I type my address or a name for it")}}</label>
            <div class=sub>{{HostPages.Field("address", "Your address or its name", "For example 203.0.113.7 or yourname.duckdns.org.",
                At(shown, "address"), $"maxlength=253 value=\"{WebUtility.HtmlEncode(typed)}\"", required: null)}}</div>
            <label class=choice><input type=radio name=choice value=lookup{{Checked(choice == HostAddress.Lookup)}}>Find my address on the internet ({{HostAddressSources.LookupService}} then sees it)</label>
            <label class=choice><input type=radio name=choice value=lan{{Checked(choice == HostAddress.Lan)}}>Only people on my home network (LAN party)</label>
            <div class=sub>{{HostPages.Field("lan", "This PC's address on your home network", "Only players on the same router or Wi-Fi can join.",
                At(shown, "lan"), $"maxlength=15 placeholder=\"192.168.1.20\" value=\"{WebUtility.HtmlEncode(lan)}\"", required: null)}}</div>
            </fieldset>
            {{(place.Step > 0 ? HostUpdates.SetupQuestion(updates) : "")}}
            {{Actions(place)}}</form>
            """;
    }
}
