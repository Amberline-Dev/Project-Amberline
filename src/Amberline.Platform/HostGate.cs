using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Amberline.Platform;

/// <summary>What <see cref="HostSessions.Redeem"/> made of a launch code.</summary>
public enum HostCode { Accepted, Missing, Expired, Foreign }

/// <summary>
/// NOT RETAIL: the home host's sign-in to its own page (retail had no host page). Loopback is shared by every Windows user on the
/// PC, so the page needs a secret another user cannot read: <c>host\page.key</c> (32 random bytes) in the data folder, which is in
/// the host's own profile. Whoever opens the page (the starting process, or a second start) signs a launch code with it:
/// <c>/open?c=&lt;t&gt;.&lt;mac&gt;</c>, <c>t</c> the Unix time in milliseconds and <c>mac</c> HMAC-SHA256(key, "open" + t). The page
/// takes a code once, within 2 minutes, and trades it for a session cookie (32 random bytes). Sessions live in memory for the
/// whole process, so a new process needs a new code: a Restart is one, and its page sends the browser to
/// <c>/open</c> with a fresh code (<see cref="HostLoop.ReopenPath"/>).
/// </summary>
public sealed class HostSessions
{
    public const string CookieName = "Amberline.Host";
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(2);
    private const int KeyBytes = 32;
    private const int MacBytes = 32;
    // Codes come from processes on this PC, so they read the same clock; the leeway covers the two reads only.
    private const long SkewMilliseconds = 5_000;
    private readonly byte[] _key;
    private readonly ConcurrentDictionary<string, byte> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, byte> _used = new();

    /// <summary>Reads the key, or makes it when it is missing or damaged (which only voids the codes already handed out).</summary>
    public HostSessions(string keyFile)
    {
        try
        {
            if (File.ReadAllBytes(keyFile) is { Length: KeyBytes } key) { _key = key; return; }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        _key = RandomNumberGenerator.GetBytes(KeyBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
        File.WriteAllBytes(keyFile + ".tmp", _key);
        File.Move(keyFile + ".tmp", keyFile, true);
    }

    public static string KeyFile(string dataFolder) => Path.Combine(dataFolder, "host", "page.key");

    /// <summary>A launch code made at <paramref name="at"/>. Two codes made in the same millisecond are the same code.</summary>
    public string Code(DateTimeOffset at)
    {
        var t = at.ToUnixTimeMilliseconds();
        return t.ToString(CultureInfo.InvariantCulture) + "." + Base64Url.EncodeToString(Mac(t));
    }

    /// <summary>Trades a launch code for a new session id. Whatever a browser sends, this never throws.</summary>
    public HostCode Redeem(string? code, DateTimeOffset now, out string session)
    {
        session = "";
        var dot = code?.IndexOf('.') ?? -1;
        if (code is null || code.Length > 64 || dot < 1 ||
            !long.TryParse(code.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var t) ||
            !Base64Url.IsValid(code.AsSpan(dot + 1), out var length) || length != MacBytes)
            return HostCode.Missing;
        // A well-formed code signed with another key: another Windows user's Amberline made it (A-RUN-02).
        if (!CryptographicOperations.FixedTimeEquals(Base64Url.DecodeFromChars(code.AsSpan(dot + 1)), Mac(t))) return HostCode.Foreign;
        var nowMs = now.ToUnixTimeMilliseconds();
        var lifetime = (long)CodeLifetime.TotalMilliseconds;
        foreach (var used in _used.Keys)
            if (nowMs - used > lifetime) _used.TryRemove(used, out _);
        if (nowMs - t > lifetime || t - nowMs > SkewMilliseconds || !_used.TryAdd(t, 0)) return HostCode.Expired;
        session = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _sessions[session] = 0;
        return HostCode.Accepted;
    }

    public bool SignedIn(HttpContext context) =>
        context.Request.Cookies.TryGetValue(CookieName, out var session) && session is { Length: 43 } && _sessions.ContainsKey(session);

    private byte[] Mac(long t) => HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes("open" + t.ToString(CultureInfo.InvariantCulture)));
}

/// <summary>
/// NOT RETAIL: security addition for the <c>--home</c> Platform; the operator's start keeps only the loopback check of
/// <see cref="PlatformManagement.IsLocal"/>. Every host route (<see cref="IsHostPath"/>) answers only:
/// <list type="bullet">
/// <item>a loopback connection to the page's port that names a loopback host (a rebound DNS name and the public port get 404);</item>
/// <item>for anything but GET and HEAD, a request that is not from another site: <c>Origin</c> absent or the page's own, and
/// <c>Sec-Fetch-Site</c> absent, <c>same-origin</c> or <c>none</c> (the forms keep their antiforgery token too);</item>
/// <item>a browser holding a session from <c>/open</c> (<see cref="HostSessions"/>).</item>
/// </list>
/// No CORS header is ever sent, so another site cannot read a host page either. Batch 656: the one exception is the friends' port's
/// sign-up while Settings' switch is on (<see cref="HostSelfRegistration.Serves"/>), which keeps its own antiforgery token and limits.
/// </summary>
public static class HostGate
{
    /// <summary>The host's pages. Later host pages go under <c>/host</c>; <c>/</c> is the AIR client's public entry, never a host page.</summary>
    public static bool IsHostPath(PathString path) =>
        path.StartsWithSegments("/open") || path.StartsWithSegments("/setup") || path.StartsWithSegments("/manage") ||
        path.StartsWithSegments("/register") || path.StartsWithSegments("/host");

    public static bool SameOrigin(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) return true;
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length != 0 && site is not ("same-origin" or "none")) return false;
        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 || string.Equals(origin, "http://" + request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Where <c>/open</c> lands (batch 649): the page of a key that a reopen names in <c>to</c>, from this fixed list of the host's own
    /// pages, and otherwise <paramref name="landing"/> (the first-run steps, which lead to the dashboard once they are done). The request's
    /// text is only ever compared with the list, never used as a path, so this is no open redirect; the launch code's rules do not change.
    /// </summary>
    public static string LandingFor(string? to, string landing) => to switch
    {
        "settings" => HostSettingsPage.PagePath + "?saved=folders",
        _ => landing,
    };

    /// <summary>The gate, and <c>/open</c>, which signs the browser in and sends it to <paramref name="landing"/>.</summary>
    /// <param name="open">The host paths a stranger may reach (batch 656: <see cref="HostSelfRegistration.Serves"/>); none by default.</param>
    public static void Use(WebApplication app, int port, HostSessions sessions, string landing, Func<HttpContext, bool>? open = null)
    {
        app.Use(async (context, next) =>
        {
            if (!IsHostPath(context.Request.Path) || open?.Invoke(context) == true) { await next(context); return; }
            if (!PlatformManagement.IsLocal(context, port)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            if (!SameOrigin(context.Request))
            {
                await Refuse("Amberline refused this request",
                    "It came from another website. Use the page that Amberline opened for you.").ExecuteAsync(context);
                return;
            }
            if (context.Request.Path != "/open" && !sessions.SignedIn(context)) { await OpenFromIcon.ExecuteAsync(context); return; }
            await next(context);
        });
        app.MapGet("/open", (HttpContext context) =>
        {
            var to = LandingFor(context.Request.Query["to"].ToString(), landing);
            if (sessions.SignedIn(context)) return Results.Redirect(to);
            switch (sessions.Redeem(context.Request.Query["c"].ToString(), DateTimeOffset.UtcNow, out var session))
            {
                case HostCode.Accepted:
                    context.Response.Cookies.Append(HostSessions.CookieName, session,
                        new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });
                    return Results.Redirect(to);
                case HostCode.Expired:
                    return Refuse("This link has expired",
                        "Each link works once, for 2 minutes. Start Amberline again from its desktop icon.");
                case HostCode.Foreign:
                    return Refuse("This Amberline belongs to another Windows user on this PC",
                        "Only the Windows user who started it can open its page. (A-RUN-02)");
                default:
                    return OpenFromIcon;
            }
        });
    }

    private static readonly IResult OpenFromIcon = Refuse("Open Amberline from its desktop icon",
        "For your safety, this page opens only through Amberline. Double-click the Amberline icon.");

    private static IResult Refuse(string title, string text) =>
        HostMode.Page($"<h1>{title}</h1><p>{text}</p>", StatusCodes.Status403Forbidden);
}
