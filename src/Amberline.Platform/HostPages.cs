using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: the home host's page shell and its one stylesheet (batch 638; retail had no host page). Every host page
/// (<see cref="HostMode.Page"/>) gets the same frame: a skip link, a header with the server's name and Amberline, in the full
/// Platform a small nav, the step indicator on the first-run steps, the page in <c>main</c> and a footer with the version. The
/// stylesheet is <see cref="StylePath"/>, a host route behind <see cref="HostGate"/>; nothing on a host page is an inline style
/// or an inline script, so the pages' policy allows only <c>style-src 'self'</c>. System fonts only: Amberline works offline.
/// </summary>
public static class HostPages
{
    public const string StylePath = "/host/style.css";

    /// <summary>
    /// The stylesheet's address on the pages: <see cref="StylePath"/> with its content's hash. The browser keeps it (batch 643), so the
    /// pages shown as Amberline restarts or stops, whose listener may already be closed, still have it; a changed stylesheet is a new address.
    /// </summary>
    public static readonly string StyleHref = Versioned(StylePath, Css);

    /// <summary>The script every shelled page loads (<see cref="Script"/>): the dashboard's status, Copy, and the wait feedback (batch 652).</summary>
    public const string ScriptPath = "/host/page.js";

    /// <summary>
    /// The policy of setup mode's pages: this origin's stylesheet and <see cref="ScriptPath"/>, nothing inline. Batch 652: the script
    /// runs here too, so the folder step's slow buttons say what they are doing.
    /// </summary>
    public const string Policy = "default-src 'none'; style-src 'self'; script-src 'self'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>The first-run steps (<see cref="HostSteps"/> and <see cref="HostFolders"/>), in order.</summary>
    public static readonly string[] Steps = ["Name your server", "Your game folders", "Your own login", "Your address"];

    private static readonly (string Path, string Text)[] Menu =
        [(HostDashboard.PagePath, "Dashboard"), (HostInvite.InvitePath, "Invite a friend"), (HostInvite.FriendsPath, "Friends"),
         (HostSettingsPage.PagePath, "Settings"), ("/manage", "Advanced"), (HostDashboard.PagePath + "/updates", "Updates")];

    /// <summary>An error code such as (A-NET-01) in a page's text, never inside a tag or an attribute.</summary>
    private static readonly Regex Code = new(@"(?<=>[^<]*)\(A-[A-Z]+-\d{2}\)");

    private sealed record Shell(Func<string?> Name, string Version, bool Nav);

    /// <summary>
    /// The stylesheet's route, and the shell for every later page: the server's name, the version and, with <paramref name="nav"/>
    /// (the full Platform), the nav. Called after <see cref="HostGate.Use"/>, so the gate's refusal pages stay bare: a browser
    /// without the host's session sees neither the name nor the nav.
    /// </summary>
    public static void Use(WebApplication app, HostLoop loop, Func<string?> name, bool nav)
    {
        var version = HostVersion.Read(Path.GetFullPath(Path.Combine(loop.Paths.App, ".."))).Version;
        var shell = new Shell(name, version?.ToString() ?? "(development build)", nav);
        app.Use(async (context, next) =>
        {
            context.Items[typeof(Shell)] = shell;
            // Advanced is the operator's /manage, shelled in home mode: the host pages' policy, as page.js updates its table (batch 646).
            if (context.Request.Path.StartsWithSegments("/manage")) context.Response.Headers.ContentSecurityPolicy = HostDashboard.Policy;
            try { await next(context); }
            // Batch 657: any save whose settings could not be read wrote nothing (HostMode.Change); the page says so, not a 500.
            catch (SettingsUnreadableException) when (!context.Response.HasStarted)
            {
                await HostMode.Page($"<h1>Not saved</h1><p>{HostMode.Unreadable}</p><div class=actions><a class=button href=\"" +
                    $"{WebUtility.HtmlEncode(context.Request.Path.Value)}\">Try again</a></div>", StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
            }
        });
        app.MapGet(StylePath, Stylesheet);
        app.MapGet(ScriptPath, () => Results.Text(Script, "text/javascript; charset=utf-8"));
    }

    /// <summary>
    /// Batch 652: an attribute that makes a slow form, button or link say at once what it is doing (<see cref="Script"/>). The
    /// sentence starts with the action's -ing word, which the pressed button then shows ("Starting…"), and a live region beside it
    /// says the whole sentence. Without the script the form posts as before.
    /// </summary>
    internal static string Wait(string sentence) => $" data-wait=\"{WebUtility.HtmlEncode(sentence)}\"";

    internal static IResult Page(string content, int status, string? refresh, int step, bool nav) => new Rendered(content, status, refresh, step, nav, false);

    /// <summary>
    /// Batch 656: the stylesheet's address on the friends' port's sign-up page (<see cref="HostSelfRegistration"/>): the same sheet and
    /// version, outside <c>/host</c>, which that port never serves.
    /// </summary>
    public const string PublicStylePath = "/register/style.css";
    public static readonly string PublicStyleHref = Versioned(PublicStylePath, PublicCss);

    /// <summary>The stylesheet's address: its path with the first twelve hex digits of its content hash (the browser keeps it).</summary>
    private static string Versioned(string path, string css) =>
        path + "?v=" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(css)))[..12].ToLowerInvariant();

    /// <summary>The host pages' stylesheet, at <see cref="StylePath"/>; the browser keeps it (see <see cref="StyleHref"/>).</summary>
    internal static IResult Stylesheet(HttpContext context) => Sheet(context, Css);

    /// <summary>Batch 706: the sign-up pages' own stylesheet, at <see cref="PublicStylePath"/> (served on the friends' port only).</summary>
    internal static IResult PublicStylesheet(HttpContext context) => Sheet(context, PublicCss);

    private static IResult Sheet(HttpContext context, string css)
    {
        context.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return Results.Text(css, "text/css; charset=utf-8");
    }

    /// <summary>
    /// Batch 656: a page of the sign-up on the friends' port, in the host pages' frame and stylesheet (<see cref="PublicStyleHref"/>) but
    /// without the nav, the script and the version, so nothing on it names or links a host page. It sets its own headers (<see cref="Policy"/>):
    /// the rate limiter's refusal is written before the Platform's header middleware runs.
    /// </summary>
    internal static IResult PublicPage(string content, int status = StatusCodes.Status200OK) => new Rendered(content, status, null, 0, false, true);

    private sealed record Rendered(string Content, int Status, string? Refresh, int Step, bool Nav, bool Public) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            if (Public)
            {
                context.Response.Headers.ContentSecurityPolicy = Policy;
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
            }
            context.Response.StatusCode = Status;
            context.Response.ContentType = "text/html; charset=utf-8";
            return context.Response.WriteAsync(Html(context.Items[typeof(Shell)] as Shell, context.Request.Path));
        }

        private string Html(Shell? shell, PathString path)
        {
            var heading = Regex.Match(Content, "<h1[^>]*>(.*?)</h1>", RegexOptions.Singleline);
            var title = (Content.Contains("class=error-summary", StringComparison.Ordinal) ? "Error: " : "") +
                (heading.Success ? Regex.Replace(heading.Groups[1].Value, "<[^>]+>", "") + " · " : "") + "Amberline";
            var name = shell?.Name() is { Length: > 0 } server ? $"<strong>{WebUtility.HtmlEncode(server)}</strong> · " : "";
            var menu = shell is { Nav: true } && Nav && Step == 0
                ? "<nav aria-label=\"Amberline\"><ul>" + string.Concat(Menu.Select(item =>
                    $"<li><a href={item.Path}{(Current(path, item.Path) ? " aria-current=page" : "")}>{item.Text}</a></li>")) + "</ul></nav>"
                : "";
            var step = Step > 0 ? $"<p class=step>Step {Step} of {Steps.Length}: {Steps[Step - 1]}</p>" : "";
            // Every page loads page.js (batch 652) but the gate's refusals (403): a browser without the host's session cannot load it.
            return $$"""
                <!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content="width=device-width, initial-scale=1">
                {{(Refresh is null ? "" : $"<meta http-equiv=refresh content=\"5;url={Refresh}\">")}}<title>{{title}}</title><link rel=stylesheet href="{{(Public ? PublicStyleHref : StyleHref)}}"></head>
                <body><a class=skip href=#main>Skip to the main content</a>
                {{(Public ? "<header><p class=wordmark>Amberline</p></header>" : $"<header><p class=brand>{name}Amberline</p>{menu}</header>")}}
                <main id=main tabindex=-1>{{step}}{{Code.Replace(Content, "<span class=code>$0</span>")}}</main>
                {{(Public ? "" : $"<footer><p>Amberline {shell?.Version}</p></footer>")}}{{(Public || Status == StatusCodes.Status403Forbidden ? "" : $"<script src={ScriptPath} defer></script>")}}</body></html>
                """;
        }

        private static bool Current(PathString path, string item) =>
            path == item || item != HostDashboard.PagePath && path.StartsWithSegments(item);
    }

    /// <summary>
    /// The error summary, first on a form page after its heading: it takes the focus when the page opens, and each problem links to its
    /// field (a null field: nothing to fix on the page, such as an expired page). The same problem also stands beside its field.
    /// </summary>
    internal static string Errors(params (string? Text, string? Field)[] problems)
    {
        var items = string.Concat(problems.Where(problem => !string.IsNullOrEmpty(problem.Text)).Select(problem => problem.Field is null
            ? $"<li>{WebUtility.HtmlEncode(problem.Text)}</li>" : $"<li><a href=#{problem.Field}>{WebUtility.HtmlEncode(problem.Text)}</a></li>"));
        return items.Length == 0 ? ""
            : $"<div class=error-summary role=alert tabindex=-1 autofocus aria-labelledby=error-title><h2 id=error-title>There is a problem</h2><ul>{items}</ul></div>";
    }

    /// <summary>
    /// A text field named <paramref name="id"/>: its label (marked required or optional), its hint, its problem and the input, which
    /// gets <paramref name="attributes"/> after its id and name. <paramref name="label"/> and <paramref name="hint"/> are markup. The
    /// field's name is its id unless <paramref name="name"/> says otherwise (Advanced's forms post the operator's names).
    /// </summary>
    internal static string Field(string id, string label, string? hint, string? problem, string attributes, bool? required = true, string? name = null) =>
        $"<label for={id}>{label}{(required is { } needed ? $" <span class=req>({(needed ? "required" : "optional")})</span>" : "")}</label>" +
        (hint is null ? "" : $"<p class=hint id={id}-hint>{hint}</p>") + FieldError(id, problem) +
        $"<input id={id} name={name ?? id} {attributes}{Described(id, hint is not null, problem)}>";

    /// <summary>The problem beside field <paramref name="id"/>, when it has one.</summary>
    internal static string FieldError(string id, string? problem) => problem is null ? ""
        : $"<p class=field-error id={id}-error><span class=vh>Error: </span>{WebUtility.HtmlEncode(problem)}</p>";

    /// <summary>A field's <c>aria-describedby</c> (its hint <c>&lt;id&gt;-hint</c> and its problem) and, with a problem, <c>aria-invalid</c>.</summary>
    internal static string Described(string id, bool hint, string? problem) =>
        (hint || problem is not null ? $" aria-describedby=\"{(hint ? id + "-hint" : "")}{(hint && problem is not null ? " " : "")}{(problem is null ? "" : id + "-error")}\"" : "") +
        (problem is null ? "" : " aria-invalid=true");

    /// <summary>
    /// A refused login (<see cref="PlatformAccounts.RegisterPublic"/>) in the host's words, and the field it belongs to. The store's own
    /// texts serve the AIR client and the operator's registration too, so they stay as they are.
    /// </summary>
    internal static (string Text, string? Field) LoginProblem(string message) => message switch
    {
        _ when message.StartsWith("Username must", StringComparison.Ordinal) =>
            ("Choose a username of 3 to 24 letters (a to z), digits or _, without spaces.", "username"),
        _ when message.StartsWith("Summoner name must", StringComparison.Ordinal) =>
            ("Choose a summoner name of 1 to 27 letters (a to z), digits, spaces or common symbols.", "summoner"),
        _ when message.StartsWith("Password must", StringComparison.Ordinal) => ("Choose a password of at least 10 characters.", "password"),
        _ when message.Contains("already taken", StringComparison.Ordinal) =>
            ("This username or summoner name is already taken. Choose another one.", "username"),
        _ when message.StartsWith("Choose where", StringComparison.Ordinal) => (message, "mode"),
        _ => (message, null),
    };

    /// <summary>
    /// The one stylesheet. Light and dark through <c>prefers-color-scheme</c>; every text colour is at least 4.5:1 on its background
    /// (borders and focus at least 3:1); buttons are at least 44 px high; one column that fits 360 px and 200% zoom.
    /// </summary>
    /// <summary>Batch 706: the sign-up pages' stylesheet (CSS only): the AIR client's dark navy frame and gold trim, system fonts.</summary>
    private const string PublicCss = """
        :root{color-scheme:dark;--top:#13233a;--bottom:#03060c;--panel:#0b1728;--inner:#060e1a;--gold:#c9ab6f;--gold-dim:#6f5a31;
        --gold-hi:#f3e2b3;--text:#e9e2d0;--muted:#a3a9b5;--field:#040a13;--line:#4d5d73;--focus:#f2d98e;--bad:#f2978b;--bad-bg:#2b1414;
        --ok:#a6dcae;--ok-bg:#122418}
        *,*::before,*::after{box-sizing:border-box}
        [hidden]{display:none!important}
        html{-webkit-text-size-adjust:100%;text-size-adjust:100%;background:var(--bottom)}
        body{margin:0;padding:48px 16px;min-height:100vh;color:var(--text);background:radial-gradient(120% 70% at 50% 0%,var(--top),var(--bottom) 75%);
        font:1rem/1.55 system-ui,-apple-system,"Segoe UI",Roboto,"Helvetica Neue",Arial,sans-serif}
        main{max-width:30rem;margin:0 auto;padding:28px 24px 30px;border:1px solid var(--gold-dim);border-radius:4px;overflow-wrap:anywhere;
        text-align:center;background:linear-gradient(180deg,var(--panel),var(--inner));box-shadow:inset 0 0 0 1px rgba(201,171,111,.14),0 16px 40px rgba(0,0,0,.55)}
        main:focus{outline:none}
        form{text-align:left}
        header{margin:0 0 26px;text-align:center}
        .wordmark{position:relative;display:inline-block;margin:0 -.3em 0 0;padding:0 0 20px;color:var(--gold);letter-spacing:.3em;text-transform:uppercase;
        font:600 clamp(1.9rem,8.5vw,2.75rem)/1.1 "Palatino Linotype","Book Antiqua",Palatino,Georgia,serif;
        background:linear-gradient(105deg,transparent 42%,rgba(255,248,224,.95) 50%,transparent 58%) 160% 0/250% 100% no-repeat,
        linear-gradient(180deg,#fff4d2 0%,#f1d48c 30%,#c99c4c 52%,#8b6327 58%,#d7b36a 78%,#f7e6b0 100%);
        -webkit-background-clip:text;background-clip:text;-webkit-text-fill-color:transparent;
        filter:drop-shadow(0 0 12px rgba(201,171,111,.35)) drop-shadow(0 2px 0 rgba(0,0,0,.9));animation:sheen 7s ease-in-out 1s infinite}
        .wordmark::before,.wordmark::after{content:"";position:absolute;left:50%}
        .wordmark::before{bottom:3px;width:9px;height:9px;background:var(--gold-hi);box-shadow:0 0 10px var(--gold);transform:translateX(calc(-50% - .15em)) rotate(45deg)}
        .wordmark::after{bottom:7px;width:115%;height:1px;transform:translateX(calc(-50% - .15em));
        background:linear-gradient(90deg,transparent,var(--gold) 22%,var(--gold) 44%,transparent 46.5%,transparent 53.5%,var(--gold) 56%,var(--gold) 78%,transparent)}
        @keyframes sheen{0%,55%{background-position:160% 0,0 0}100%{background-position:-60% 0,0 0}}
        @media (prefers-reduced-motion:reduce){.wordmark{animation:none}}
        .skip{position:absolute;top:-100px;left:16px;z-index:1;padding:8px 12px;background:var(--panel);color:var(--gold);font-weight:600}
        .skip:focus{top:8px}
        :focus-visible{outline:2px solid var(--focus);outline-offset:2px}
        h1{margin:0 0 22px;color:var(--gold);font-size:1.125rem;line-height:1.35;font-weight:600;letter-spacing:.16em;text-align:center;text-transform:uppercase}
        h2{margin:0 0 8px;color:var(--gold);font-size:.9375rem;letter-spacing:.12em;text-transform:uppercase}
        p,ul,ol,pre{max-width:65ch;margin:0 0 14px}
        a{color:var(--gold)}
        a:hover{color:var(--gold-hi)}
        .tip,.hint,.saved,.already{color:var(--muted)}
        .code{color:var(--muted);font-size:.8125rem;white-space:nowrap}
        .step{margin:0 0 6px;color:var(--muted);font-size:.8125rem;font-weight:600;letter-spacing:.12em;text-transform:uppercase}
        .vh{position:absolute!important;width:1px;height:1px;margin:-1px;padding:0;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap;border:0}
        form{margin:0}
        label{display:block;margin:18px 0 6px;font-weight:600;letter-spacing:.02em}
        .req{font-weight:400;color:var(--muted)}
        .hint{margin:0 0 6px;font-size:.9375rem}
        input:not([type=radio]):not([type=checkbox]),select{display:block;width:100%;min-height:44px;padding:8px 10px;border:1px solid var(--line);
        border-radius:2px;background:var(--field);color:var(--text);font:inherit;box-shadow:inset 0 1px 3px rgba(0,0,0,.6)}
        input:not([type=radio]):not([type=checkbox]):focus,select:focus{border-color:var(--gold);outline:none;box-shadow:inset 0 1px 3px rgba(0,0,0,.6),0 0 0 1px var(--gold)}
        input[aria-invalid=true]:not([type=radio]):not([type=checkbox]){border-color:var(--bad);box-shadow:inset 0 1px 3px rgba(0,0,0,.6),0 0 0 1px var(--bad)}
        ::placeholder{color:var(--muted);opacity:.85}
        fieldset{margin:22px 0 0;padding:0;border:0}
        legend{margin:0 0 6px;padding:0;font-weight:600}
        .choice{display:flex;gap:10px;align-items:flex-start;margin:10px 0 4px;font-weight:400}
        .choice input{flex:none;width:22px;height:22px;margin:2px 0 0;accent-color:var(--gold)}
        button,.button{display:inline-flex;align-items:center;justify-content:center;min-height:44px;padding:8px 22px;border:1px solid var(--gold);
        border-radius:2px;background:linear-gradient(180deg,#1d2e45,#0a1322);color:var(--gold-hi);font:inherit;font-weight:600;letter-spacing:.12em;
        line-height:1.3;text-align:center;text-transform:uppercase;text-decoration:none;cursor:pointer;box-shadow:inset 0 1px 0 rgba(255,255,255,.1),0 3px 0 #3b2c13}
        button:hover,.button:hover{color:#fff;box-shadow:inset 0 1px 0 rgba(255,255,255,.12),0 3px 0 #3b2c13,0 0 16px rgba(201,171,111,.5)}
        button:active,.button:active{transform:translateY(2px);box-shadow:inset 0 1px 0 rgba(255,255,255,.06),0 1px 0 #3b2c13}
        button:disabled{cursor:progress;opacity:.7}
        .actions{display:flex;flex-wrap:wrap;gap:12px;align-items:center;justify-content:center;margin-top:26px;padding-top:18px;border-top:1px solid var(--gold-dim)}
        .already{margin:22px 0 10px}
        .notice,.problem,.error-summary{position:relative;max-width:none;margin:0 0 22px;padding:12px 16px 12px 46px;border:1px solid;border-radius:2px;text-align:left}
        .notice{border-color:var(--ok);background:var(--ok-bg)}
        .problem,.error-summary{border-color:var(--bad);background:var(--bad-bg)}
        .notice::before,.problem::before,.error-summary::before{position:absolute;left:14px;top:13px;width:20px;height:20px;border:1px solid;border-radius:50%;
        font:700 .75rem/18px system-ui,sans-serif;text-align:center}
        .notice::before{content:"\2713";color:var(--ok)}
        .problem::before,.error-summary::before{content:"\2717";color:var(--bad)}
        .error-summary h2{margin:0 0 2px;color:var(--bad);font-size:.875rem}
        .error-summary ul{margin:0;padding:0;list-style:none}
        .error-summary a{color:var(--text);text-decoration-color:var(--bad)}
        .field-error{position:absolute!important;width:1px;height:1px;margin:-1px;padding:0;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap;border:0}
        @media (max-width:23rem){main{padding-left:16px;padding-right:16px}h1{font-size:1rem}}
        @media (forced-colors:active){button,.button{border-color:ButtonText}.problem,.notice,.error-summary{border-color:CanvasText}
        .wordmark{background:none;-webkit-text-fill-color:CanvasText;filter:none}}
        """;

    private const string Css = """
        :root{color-scheme:light dark;--bg:#f7f5f1;--surface:#fff;--text:#1f1f1f;--muted:#575757;--line:#6b6b6b;--soft:#dcd7cd;
        --accent:#8a4f0e;--on-accent:#fff;--link:#1a5fb4;--focus:#1a5fb4;--bad:#b3261e;--bad-bg:#fbe9e7;--ok:#1e6b2e;--ok-bg:#e6f2e8;
        --warn:#7a5000;--warn-bg:#fdf3dc}
        @media (prefers-color-scheme:dark){:root{--bg:#17191c;--surface:#202328;--text:#ececec;--muted:#b4b4b4;--line:#9a9a9a;--soft:#3a3d42;
        --accent:#f0b45a;--on-accent:#1a1a1a;--link:#8ab4f8;--focus:#8ab4f8;--bad:#ff8a80;--bad-bg:#3a1f1d;--ok:#7bd88f;--ok-bg:#1d3324;
        --warn:#f0c674;--warn-bg:#3a3120}}
        *,*::before,*::after{box-sizing:border-box}
        [hidden]{display:none!important}
        html{-webkit-text-size-adjust:100%;text-size-adjust:100%}
        body{margin:0;background:var(--bg);color:var(--text);font:1rem/1.6 system-ui,-apple-system,"Segoe UI",Roboto,"Helvetica Neue",Arial,sans-serif}
        header,main,footer{max-width:46rem;margin:0 auto;padding-left:16px;padding-right:16px}
        header{padding-top:16px;padding-bottom:8px;border-bottom:1px solid var(--soft)}
        main{padding-top:24px;padding-bottom:40px;overflow-wrap:anywhere}
        main:focus{outline:none}
        footer{padding-top:12px;padding-bottom:24px;border-top:1px solid var(--soft);color:var(--muted);font-size:.875rem}
        .brand{margin:0;font-size:1.0625rem}
        nav ul{display:flex;flex-wrap:wrap;gap:4px 6px;max-width:none;margin:8px 0 0;padding:0;list-style:none}
        nav a{display:inline-block;min-height:24px;padding:6px 10px;border-radius:6px;text-decoration:none}
        nav a:hover{text-decoration:underline}
        nav a[aria-current=page]{background:var(--surface);color:var(--text);font-weight:600;box-shadow:inset 0 -3px 0 var(--accent)}
        .skip{position:absolute;top:-100px;left:16px;z-index:1;padding:8px 12px;background:var(--surface);color:var(--link);font-weight:600}
        .skip:focus{top:8px}
        :focus-visible{outline:3px solid var(--focus);outline-offset:2px}
        h1{margin:0 0 16px;font-size:1.75rem;line-height:1.25}
        h2{margin:32px 0 12px;font-size:1.25rem;line-height:1.3}
        p,ul,ol,pre{max-width:65ch;margin:0 0 16px}
        li{margin:4px 0}
        a{color:var(--link)}
        .step{margin:0 0 4px;color:var(--muted);font-weight:600}
        .tip,.hint,.saved{color:var(--muted)}
        .code{color:var(--muted);font-size:.8125rem;white-space:nowrap}
        .vh{position:absolute!important;width:1px;height:1px;margin:-1px;padding:0;overflow:hidden;clip:rect(0 0 0 0);white-space:nowrap;border:0}
        form{margin:0 0 16px}
        label{display:block;margin:20px 0 4px;font-weight:600}
        .req{font-weight:400;color:var(--muted)}
        .hint{margin:0 0 6px}
        input:not([type=radio]):not([type=checkbox]),select{display:block;width:100%;max-width:32rem;min-height:44px;padding:8px 10px;
        border:2px solid var(--line);border-radius:6px;background:var(--surface);color:var(--text);font:inherit}
        textarea{display:block;width:100%;min-height:12rem;margin:0 0 12px;padding:8px 10px;border:2px solid var(--line);border-radius:6px;
        background:var(--surface);color:var(--text);font:.875rem/1.5 ui-monospace,Consolas,monospace;resize:vertical}
        input[aria-invalid=true]:not([type=radio]):not([type=checkbox]){border-color:var(--bad)}
        ::placeholder{color:var(--muted);opacity:1}
        fieldset{margin:20px 0 0;padding:0;border:0}
        legend{margin:0 0 4px;padding:0;font-weight:600}
        .choice{display:flex;gap:10px;align-items:flex-start;margin:12px 0 4px;font-weight:400}
        .choice input{flex:none;width:24px;height:24px;margin:0;accent-color:var(--accent)}
        .sub{margin:0 0 8px 2rem}
        .sub label{margin-top:6px}
        button,.button,.actions a{display:inline-flex;align-items:center;justify-content:center;min-height:44px;padding:8px 18px;
        border:2px solid var(--accent);border-radius:6px;background:var(--accent);color:var(--on-accent);font:inherit;font-weight:600;
        line-height:1.3;text-align:center;text-decoration:none;cursor:pointer}
        button:hover,.button:hover,.actions a:hover{text-decoration:underline}
        button:disabled{cursor:progress;opacity:.75;text-decoration:none}
        .wait{margin:12px 0 0;font-weight:600}
        .wait:empty{display:none}
        .secondary,button.secondary,button[data-copy],.actions a:not(.button){background:transparent;color:var(--accent)}
        td button{min-height:36px;padding:4px 12px;overflow-wrap:normal}
        td form{margin:0}
        .buttons{display:flex;flex-wrap:wrap;gap:12px}
        .choice+.buttons{margin-top:16px}
        .actions{display:flex;flex-wrap:wrap;gap:12px;align-items:center;margin-top:28px;padding-top:16px;border-top:1px solid var(--soft)}
        .actions>:last-child{margin-left:auto}
        .problem,.warn,.notice{max-width:none;padding:12px 16px;border-left:5px solid;border-radius:4px}
        .problem{border-color:var(--bad);background:var(--bad-bg)}
        .warn{border-color:var(--warn);background:var(--warn-bg)}
        .notice{border-color:var(--ok);background:var(--ok-bg)}
        .problem::before{content:"\2717  ";content:"\2717  "/"";font-weight:700;color:var(--bad)}
        .warn::before{content:"!  ";content:"!  "/"";font-weight:700;color:var(--warn)}
        .notice::before{content:"\2713  ";content:"\2713  "/"";font-weight:700;color:var(--ok)}
        .error-summary{margin:0 0 24px;padding:12px 16px;border:3px solid var(--bad);border-radius:6px;background:var(--surface)}
        .error-summary h2{margin:0 0 8px;font-size:1.125rem}
        .error-summary ul{margin:0;padding-left:20px}
        .error-summary a{color:var(--bad);font-weight:600}
        .field-error{margin:0 0 6px;color:var(--bad);font-weight:600}
        .field-error::before{content:"\2717  ";content:"\2717  "/""}
        .ok{color:var(--ok);font-weight:600}
        .no,.bad{color:var(--bad);font-weight:600}
        .folder,.status{margin:0 0 16px;padding:12px 16px;border:1px solid var(--soft);border-radius:8px;background:var(--surface);overflow-wrap:anywhere}
        .folder h2,.status h2{margin:0 0 4px;font-size:1.0625rem}
        .folder form{margin:8px 0 0}
        .headline{font-size:1.125rem}
        progress{display:block;width:100%;max-width:32rem;height:1.5rem;margin:0 0 8px;accent-color:var(--accent)}
        .checks,.cards{padding-left:0;list-style:none}
        .cards{max-width:none}
        .checks li{display:flex;gap:8px}
        .mark{flex:none;width:1.25rem;font-weight:700}
        .checks .ok,.checks .bad{font-weight:400;color:var(--text)}
        .checks .ok .mark{color:var(--ok)}
        .checks .bad .mark{color:var(--bad)}
        .cards li{margin:0 0 12px;padding:12px 16px;border-left:5px solid var(--bad);border-radius:4px;background:var(--bad-bg)}
        .cards p{margin:4px 0 0}
        table{width:100%;margin:0 0 16px;border-collapse:collapse}
        th,td{padding:8px 12px 8px 0;border-bottom:1px solid var(--soft);text-align:left;vertical-align:top;overflow-wrap:anywhere}
        th{font-weight:600;overflow-wrap:normal}
        td.words{overflow-wrap:normal}
        .wide{overflow-x:auto;margin:0 0 16px}
        .wide>table{margin:0}
        .wide:has(>table[hidden]){display:none}
        @media (max-width:23rem){.wide th,.wide td{padding-right:8px}.wide td button{padding-left:8px;padding-right:8px}}
        .message{padding:12px 16px;border:1px solid var(--line);border-radius:6px;background:var(--surface);white-space:pre-wrap;
        overflow-wrap:anywhere;font:.9375rem/1.5 ui-monospace,Consolas,monospace}
        .notes{white-space:pre-wrap}
        @media (max-width:30rem){h1{font-size:1.5rem}.sub{margin-left:1rem}}
        @media (forced-colors:active){button,.button,.actions a{border-color:ButtonText}.problem,.warn,.notice,.cards li{border-color:CanvasText}}
        """;

    /// <summary>
    /// The host pages' one script (<see cref="ScriptPath"/>, on every shelled page). It fills the dashboard's status card from
    /// <c>/host/status.json</c> every 5 seconds without reloading, so nothing typed on a page is lost, as it fills Advanced's Online now
    /// table (<see cref="PlatformManagement.OnlineJsonPath"/>, while the table is <c>data-live</c>); it runs the Copy buttons
    /// (<c>data-copy</c> names the element whose text is copied), batch 652, the wait feedback of <see cref="Wait"/>, and, batch 666,
    /// Update now's progress (<see cref="HostUpdates.StatusPath"/>, every second while the page has its bar). It only
    /// ever sets text, never markup, and a text only when it changed (the headline is a live region). A Copy button says "Copied" on
    /// itself and in the <c>copy-status</c> live region beside it; when the browser cannot copy, it selects the text.
    /// </summary>
    private const string Script = """
        'use strict';
        (() => {
          const byId = id => document.getElementById(id);
          const text = (id, value) => { const element = byId(id); if (element && element.textContent !== value) element.textContent = value; };
          const made = (tag, value, kind) => { const element = document.createElement(tag); element.textContent = value; if (kind) element.className = kind; return element; };
          const fill = (id, items, make) => { const element = byId(id); if (element) element.replaceChildren(...items.map(make)); };
          const show = (id, visible) => { const element = byId(id); if (element) element.hidden = !visible; };
          const marks = { ok: '✓', bad: '✗', unknown: '?', waiting: '…', checking: '…' };
          function render(status) {
            const states = status.checks.map(check => check.state);
            text('status-mark', states.includes('bad') ? '!' : states.includes('checking') ? '…' : '✓');
            text('headline', status.headline);
            text('address', status.address);
            text('network-line', status.network);
            text('friend', status.friend);
            text('online-title', `Online now (${status.online.length})`);
            show('online-empty', status.online.length === 0);
            text('online-empty', 'Nobody is online.');
            show('online-table', status.online.length > 0);
            fill('online', status.online, person => {
              const row = document.createElement('tr');
              row.append(made('td', person.username), made('td', person.doing), made('td', person.since));
              return row;
            });
            text('games-title', `Games (${status.gamesRunning} of ${status.gamesMax})`);
            show('games-empty', status.games.length === 0);
            text('games-empty', 'No game is running.');
            fill('games', status.games, game => made('li', game));
            fill('checks', status.checks, check => {
              const item = made('li', '', check.state);
              const mark = made('span', marks[check.state] ?? '?', 'mark');
              mark.setAttribute('aria-hidden', 'true');
              item.append(mark, made('span', check.text));
              return item;
            });
            show('attention-none', status.attention.length === 0);
            text('attention-none', 'Nothing needs your attention.');
            fill('attention', status.attention, card => {
              const item = document.createElement('li');
              item.append(made('strong', card.message), ' ', made('span', `(${card.code})`, 'code'), made('p', card.whatToDo));
              return item;
            });
            for (const form of document.querySelectorAll('form[data-action]'))
              form.hidden = !status.attention.some(card => card.action === form.dataset.action);
            show('guide', Boolean(status.guide));
            fill('guide-steps', status.guide ?? [], step => made('li', step));
          }
          const gone = 'Amberline does not answer. If it stopped, double-click Amberline on your desktop.';
          async function refresh() {
            try {
              const answer = await fetch('/host/status.json', { cache: 'no-store' });
              if (!answer.ok) throw new Error(String(answer.status));
              render(await answer.json());
            } catch {
              text('headline', gone);
            }
          }
          // Advanced (batch 646): only the Online now table and its live count line change; no form field is ever touched.
          async function online() {
            try {
              const answer = await fetch('/manage/online.json', { cache: 'no-store' });
              if (!answer.ok) throw new Error(String(answer.status));
              const rows = await answer.json();
              text('online-count', rows.length === 0 ? 'Nobody is logged in.' : rows.length === 1 ? '1 person is logged in.' : `${rows.length} people are logged in.`);
              show('online-table', rows.length > 0);
              fill('online-rows', rows, person => {
                const row = document.createElement('tr');
                row.append(...['username', 'summoner', 'address', 'doing', 'since', 'chat'].map(key => made('td', person[key])));
                return row;
              });
            } catch {
              text('online-count', gone);
            }
          }
          document.addEventListener('click', event => {
            const button = event.target.closest('button[data-copy]');
            const source = button && byId(button.dataset.copy);
            if (!source) return;
            const said = value => { const status = byId('copy-status'); if (status) status.textContent = value; };
            const failed = () => {
              if (source.select) source.select(); else getSelection().selectAllChildren(source);
              said('Amberline could not copy it. It is selected now: press Ctrl+C.');
            };
            if (!navigator.clipboard) { failed(); return; }
            navigator.clipboard.writeText(source.textContent).then(() => { button.textContent = 'Copied'; said('Copied'); }, failed);
          });
          // Batch 652, the wait feedback: each form, button or link with data-wait gets a live region (made now, so it exists before it
          // speaks). Pressed, the button shows the sentence's first word ("Starting…") and the region says the whole sentence.
          const regions = new Map();
          for (const element of document.querySelectorAll('[data-wait]')) {
            const place = element.closest('form') ?? element.parentElement;
            if (regions.has(place)) continue;
            const region = made('p', '', 'wait');
            region.setAttribute('role', 'status');
            place.append(region);
            regions.set(place, region);
          }
          const busy = [];
          function waiting(place, control, sentence) {
            const region = regions.get(place);
            if (region) region.textContent = sentence;
            if (control) {
              busy.push([control, control.textContent]);
              control.textContent = sentence.split(' ')[0] + '…';
            }
          }
          document.addEventListener('submit', event => {
            const form = event.target;
            const sentence = event.submitter?.dataset.wait ?? form.dataset.wait;
            if (!sentence) return;
            if (form.dataset.busy) { event.preventDefault(); return; }
            form.dataset.busy = 'yes';
            // After the browser has read the form: a button disabled now would leave its name and value out of it.
            setTimeout(() => {
              waiting(form, event.submitter, sentence);
              for (const button of form.querySelectorAll('button')) { button.disabled = true; busy.push([button, null]); }
            });
          });
          document.addEventListener('click', event => {
            const link = event.target.closest('a[data-wait]');
            if (link && !event.defaultPrevented && event.button === 0 && !event.ctrlKey && !event.metaKey && !event.shiftKey)
              waiting(link.parentElement, link, link.dataset.wait);
          });
          // Back to a page the browser kept: its buttons work again and its waits are cleared.
          addEventListener('pageshow', event => {
            if (!event.persisted) return;
            for (const [control, label] of busy.splice(0)) { if (label === null) control.disabled = false; else control.textContent = label; }
            for (const form of document.querySelectorAll('form[data-busy]')) delete form.dataset.busy;
            for (const region of regions.values()) region.textContent = '';
          });
          // Batch 666, Update now: the download's progress every second, as the folder copy's page; when it ends the page says how.
          async function update() {
            try {
              const answer = await fetch('/host/updates/install/status.json', { cache: 'no-store' });
              if (!answer.ok) throw new Error(String(answer.status));
              const status = await answer.json();
              if (status.state !== 'downloading') { location.reload(); return; }
              text('update-phase', status.phase);
              text('update-detail', status.detail);
              const bar = byId('update-bar');
              if (bar.value !== status.percent) { bar.value = status.percent; bar.textContent = `${status.percent}%`; }
            } catch {
              text('update-phase', gone);
            }
            setTimeout(update, 1000);
          }
          if (byId('status')) { refresh(); setInterval(refresh, 5000); }
          if (byId('online-table')?.hasAttribute('data-live')) setInterval(online, 5000);
          if (byId('update-bar')) setTimeout(update, 1000);
        })();
        """;
}
