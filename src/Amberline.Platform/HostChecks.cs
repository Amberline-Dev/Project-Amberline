using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using Amberline.Host;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Connections;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: the home host's start checks and their plain words (Amberline Host design, section 2.5; retail's servers were run by
/// Riot's staff). Each text says what happened and what to do, and ends with or carries its short code for an issue report. None
/// names the host's address, a password or a player's chat.
/// </summary>
public static class HostChecks
{
    /// <summary>
    /// The data format this Amberline writes (<c>dataVersion</c> in <c>platform.json</c>). A later Amberline that changes the data
    /// folder's files raises it, and this one then refuses that folder (A-DATA-01) instead of rewriting it.
    /// </summary>
    public const int DataVersion = 1;

    /// <summary>How long before the certificate runs out the dashboard warns (A-CERT-01).</summary>
    public static readonly TimeSpan CertificateWarning = TimeSpan.FromDays(60);

    /// <summary>A-NET-01: a port Amberline needs is taken; <paramref name="port"/> is null when the start could not tell which.</summary>
    public static string PortInUse(int? port) =>
        $"Another program uses a port Amberline needs{(port is null ? "" : $" ({port})")}. Is Amberline already running for another " +
        "Windows user, or a Play file for another server open? (A-NET-01)";

    /// <summary>Whether a start failed because Windows refused one of its listening ports (taken, or reserved by Windows).</summary>
    public static bool PortRefused(Exception error) => error switch
    {
        SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied } => true,
        AddressInUseException => true,
        IOException { InnerException: { } inner } => PortRefused(inner),
        _ => false,
    };

    /// <summary>
    /// The first of the Platform's listening ports that cannot be bound now, bound the way its listeners bind it (the page, RTMP and
    /// XMPP on loopback, the friends' port on the bind address), or null. It binds without listening, so Windows Firewall never asks.
    /// </summary>
    public static int? BusyPort(PlatformSettings settings)
    {
        var bind = IPAddress.TryParse(settings.BindAddress, out var address) ? address : IPAddress.Any;
        foreach (var (at, port) in new[] { (IPAddress.Loopback, settings.HttpPort), (IPAddress.Loopback, settings.RtmpPort),
                     (IPAddress.Loopback, settings.XmppPort), (bind, settings.RemotePort) })
        {
            using var socket = new Socket(at.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { socket.Bind(new IPEndPoint(at, port)); }
            catch (SocketException) { return port; }
        }
        return null;
    }

    /// <summary>
    /// A-DATA-01 when <c>platform.json</c> says a newer Amberline wrote the data folder (a <c>dataVersion</c> above
    /// <see cref="DataVersion"/>), else null. A file that is missing or not readable is setup mode's business, not this check's.
    /// </summary>
    public static string? NewerData(string settingsFile)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(settingsFile)) is JsonObject settings && settings["dataVersion"] is JsonValue value &&
                value.TryGetValue<double>(out var version) && version > DataVersion)
                return "Your settings come from a newer Amberline. Start that version, or update this one: download the newest Amberline " +
                    "and start it. Amberline did not change your settings. (A-DATA-01)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return null;
    }

    /// <summary>A-FILES-06 for a part of Amberline that is gone, named by its place in the app folder (the derived match server).</summary>
    public static string MissingPart(string app, string file) =>
        $"Windows Security may have removed {Path.GetFileName(file)}, a part of Amberline (in {Path.GetRelativePath(app, Path.GetDirectoryName(file)!)}). " +
        "Restore it in Windows Security (Protection history), or download Amberline again and start it; your settings and users stay. (A-FILES-06)";

    /// <summary>
    /// A-FILES-06: compares the app folder with its <c>BUILD.json</c> (each file's size and SHA-256) and names the first file that is
    /// missing, else the first that differs. Null when every file matches, or when there is no <c>BUILD.json</c> (a build that is not
    /// a release, such as the developer's). A file that cannot be read now is skipped: only a file that is gone or changed counts.
    /// </summary>
    public static async Task<DashboardCard?> AppFilesAsync(string app, CancellationToken cancel)
    {
        app = Path.GetFullPath(app);
        var manifest = Path.Combine(app, "BUILD.json");
        if (!File.Exists(manifest)) return null;
        List<string> missing = [], changed = [];
        try
        {
            using var build = JsonDocument.Parse(await File.ReadAllTextAsync(manifest, cancel));
            foreach (var entry in build.RootElement.GetProperty("files").EnumerateArray())
            {
                var name = entry.GetProperty("path").GetString() ?? "";
                var path = Path.GetFullPath(Path.Combine(app, name));
                if (!path.StartsWith(app + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                var file = new FileInfo(path);
                if (!file.Exists) { missing.Add(name); continue; }
                try
                {
                    if (entry.TryGetProperty("bytes", out var bytes) && bytes.GetInt64() != file.Length) { changed.Add(name); continue; }
                    await using var stream = file.OpenRead();
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancel));
                    if (!hash.Equals(entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) changed.Add(name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            changed.Add("BUILD.json");
        }
        if (missing.Count > 0)
            return new("A-FILES-06", $"Windows Security may have removed {Path.GetFileName(missing[0])}, a part of Amberline{More(missing.Count + changed.Count)}.",
                "Restore it in Windows Security (Protection history), or download Amberline again; your settings and users stay. Then press Restart.", null);
        return changed.Count == 0 ? null
            : new("A-FILES-06", $"A part of Amberline was changed: {Path.GetFileName(changed[0])}{More(changed.Count)}.",
                "Download Amberline again and start it. Your settings and users stay.", null);
    }

    private static string More(int count) => count switch { 1 => "", 2 => " (and 1 more part)", _ => $" (and {count - 1} more parts)" };

    /// <summary>A-CERT-01 within <see cref="CertificateWarning"/> of the certificate's end (installs made with the two-year one), else null.</summary>
    public static DashboardCard? CertificateCard(DateTimeOffset notAfter, DateTimeOffset now) =>
        notAfter - now > CertificateWarning ? null
        : new("A-CERT-01", notAfter <= now
                ? "Your users' Play files stopped working: Amberline's certificate ran out."
                : $"Your users' Play files stop working on {notAfter.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}. Make new ones.",
            "Choose Make new Play files, then send every user the new one.",
            "certificate");

    /// <summary>
    /// A-NET-08: the PC sends its internet traffic from another address than the one it reaches the router from, which a VPN does.
    /// Null when the two match or either is unknown.
    /// </summary>
    public static DashboardCard? VpnCard(IPAddress? computer, IPAddress? outbound) =>
        computer is null || outbound is null || computer.Equals(outbound) ? null
        : new("A-NET-08", "A VPN is on, so players may not reach you.",
            "Switch the VPN off while you host, then press Test again.", "network");

    /// <summary>A-NET-09, the address-change banner of design section 5.6.</summary>
    public static DashboardCard AddressChanged(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var when = local.Date == now.ToLocalTime().Date ? local.ToString("'at' HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("'on' d MMMM 'at' HH:mm", CultureInfo.InvariantCulture);
        return new("A-NET-09", $"Your address changed {when}. Users need the new Play file to reach you.",
            "Send every user the new Play file, then choose Hide.", "address");
    }

    /// <summary>
    /// The address this PC sends internet traffic from. "Connecting" a UDP socket sends nothing: Windows only picks the route, here to
    /// a documentation address (RFC 5737) that stands for any internet address.
    /// </summary>
    public static IPAddress? OutboundAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 9));
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException) { return null; }
    }
}

/// <summary>
/// NOT RETAIL: the dashboard's cards from the start checks (Amberline Host design, sections 2.5, 4.2, 4.4 and 5.6; retail had no host
/// page). Each full start compares the app folder with <c>BUILD.json</c> in the background (A-FILES-06), reads the certificate's end
/// (A-CERT-01) and, while the host's address is the router's (<c>addressSource</c> <c>router</c>), asks the router for it at the start
/// and every <see cref="HostLoop.AddressEvery"/>. A new public address is saved and served at once (<see cref="HostSteps.Follow"/>)
/// and raises A-NET-09 until the host hides it; only its time is kept, in <c>host\state.json</c>. The outside lookup is never asked.
/// </summary>
public sealed class HostAttention : IAsyncDisposable
{
    public const string AddressSeenPath = HostDashboard.PagePath + "/address-seen";
    public const string CertificatePath = HostDashboard.PagePath + "/certificate";

    /// <summary><c>host\state.json</c> has two writers (this and <see cref="HostFriendLight"/>), which both merge under this lock.</summary>
    internal static readonly Lock StateGate = new();

    private const string ChangedKey = "addressChangedAt";
    private static readonly TimeSpan AskFor = TimeSpan.FromSeconds(10);
    private readonly HostLoop _loop;
    private readonly Func<IReadOnlyList<DashboardGame>> _games;
    private readonly Func<CancellationToken, Task> _retest;
    private readonly string _state;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _files = Task.CompletedTask, _watch = Task.CompletedTask;
    private DashboardCard? _filesCard;
    private DateTimeOffset? _certificateEnds, _changedAt;

    /// <param name="retest">The network panel's test, run again after a change so the dashboard's address line shows the new one.</param>
    public HostAttention(HostLoop loop, Func<IReadOnlyList<DashboardGame>> games, Func<CancellationToken, Task> retest)
    {
        _loop = loop;
        _games = games;
        _retest = retest;
        _state = HostFriendLight.StateFile(loop.Paths.Data);
        _changedAt = ReadChanged();
    }

    /// <summary>
    /// Starts the checks once the listeners are up and maps the cards' two pages. <paramref name="steps"/> (null: no address watch)
    /// holds the address friends are given; <paramref name="certificate"/> is the one the public port serves.
    /// </summary>
    public void Map(WebApplication app, HostSteps? steps, X509Certificate2? certificate)
    {
        _certificateEnds = certificate is null ? null : new DateTimeOffset(certificate.NotAfter);
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            _files = CompareFilesAsync();
            if (steps is not null) _watch = WatchAsync(steps);
        });
        app.MapPost(AddressSeenPath, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context)) return Expired;
            SetChanged(null);
            return Results.Redirect(HostDashboard.PagePath);
        });
        // Batch 646: Settings links here with ?from=settings, so Keep the old ones (and Back) return there instead of to the dashboard.
        static (string Query, string Back) From(HttpContext context) => context.Request.Query["from"] == "settings"
            ? ("?from=settings", HostSettingsPage.PagePath) : ("", HostDashboard.PagePath);
        app.MapGet(CertificatePath, (HttpContext context, IAntiforgery antiforgery) => HostMode.Page($"""
            <h1>Make new Play files?</h1>
            <p>Amberline restarts, and every user's old Play file stops working at once. Send them the new one.</p>
            <form method=post action="{CertificatePath}{From(context).Query}"{HostPages.Wait("Making new Play files. Amberline restarts in a few seconds.")}>{Program.RegistrationToken(context, antiforgery)}<div class=buttons><button type=submit>Make new Play files</button>
            <a class="button secondary" href={From(context).Back}>Cancel</a></div></form>
            """));
        app.MapPost(CertificatePath, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context)) return Expired;
            if (_games().Count > 0)
                return HostMode.Page($"<h1>A game is running</h1><p>Make new Play files when it has ended.</p><p><a href={From(context).Back}>Back</a></p>",
                    StatusCodes.Status409Conflict);
            // The old certificate is kept beside the new one, never deleted; the next start makes a new one (HostFiles.Certificate).
            var pfx = Path.Combine(_loop.Paths.Data, "remote", "host.pfx");
            File.Move(pfx, $"{pfx}.replaced-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}");
            context.Response.OnCompleted(() => { _loop.Restart(); return Task.CompletedTask; });
            return HostMode.Page("<h1>Amberline is making new Play files</h1><p>This page comes back by itself.</p>", refresh: _loop.ReopenPath, nav: false);
        });
    }

    /// <summary>The cards of the checks at <paramref name="now"/>; <paramref name="net"/> is the network panel's last test.</summary>
    public IEnumerable<DashboardCard> Cards(DateTimeOffset now, NetworkResult net)
    {
        if (_filesCard is { } files) yield return files;
        if (_certificateEnds is { } ends && HostChecks.CertificateCard(ends, now) is { } certificate) yield return certificate;
        if (net.RouterDone && HostChecks.VpnCard(net.Computer, _loop.Network?.Outbound?.Invoke()) is { } vpn) yield return vpn;
        DateTimeOffset? changed;
        lock (_gate) changed = _changedAt;
        if (changed is { } at) yield return HostChecks.AddressChanged(at, now);
    }

    /// <summary>
    /// The cards' buttons, shown by the page while a card names their action. The address change (A-NET-09) has two: the new Play file
    /// (H8's download, which holds the new address) and Hide. Secondary, so Play stays the dashboard's one primary button (batch 652).
    /// </summary>
    public static string Forms(string token) =>
        $"<form method=get action={HostInvite.PlayFilePath} data-action=address hidden><button type=submit class=secondary>Download the Play file</button></form>" +
        $"<form method=post action={AddressSeenPath} data-action=address hidden>{token}<button type=submit class=secondary>Hide<span class=vh> the address message</span></button></form>" +
        $"<form method=get action={CertificatePath} data-action=certificate hidden><button type=submit class=secondary>Make new Play files</button></form>";

    /// <summary>Asks the router once: a new public address is followed when the host's choice is still the router's address.</summary>
    public async Task AskRouterAsync(HostSteps steps, CancellationToken stop)
    {
        if (HostMode.ReadOrDefault(_loop.Paths.Settings).AddressSource != HostAddress.Router) return;
        IPAddress? answer;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            wait.CancelAfter(AskFor);
            try { answer = await _loop.Addresses.Router(wait.Token); }
            catch (Exception ex) when (ex is SocketException or HttpRequestException or IOException or InvalidOperationException or XmlException ||
                                       ex is OperationCanceledException && !stop.IsCancellationRequested)
            {
                // Not answering is the network panel's card; the address stays out of the log, so only what failed is written.
                Console.WriteLine($"The router did not tell its address ({ex.GetType().Name}).");
                return;
            }
        }
        if (WanAddress.Classify(answer) != WanAddressClass.Public || !steps.Follow(answer!.ToString())) return;
        Console.WriteLine("The address the router reports changed; users need the new Play file (A-NET-09).");
        SetChanged(DateTimeOffset.Now);
        await _retest(stop);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _files;
        await _watch;
        _stop.Dispose();
    }

    private async Task CompareFilesAsync()
    {
        try { _filesCard = await Task.Run(() => HostChecks.AppFilesAsync(Path.Combine(_loop.Paths.App, ".."), _stop.Token)); }
        catch (OperationCanceledException) { }
    }

    private async Task WatchAsync(HostSteps steps)
    {
        try
        {
            using var timer = new PeriodicTimer(_loop.AddressEvery);
            do
            {
                // The watch runs on its own: a failed ask is logged and the next period asks again.
                try { await AskRouterAsync(steps, _stop.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Console.Error.WriteLine($"Amberline's address check failed: {ex.GetType().Name}"); }
            }
            while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException) { }
    }

    private DateTimeOffset? ReadChanged()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(_state)) is JsonObject state && state[ChangedKey] is JsonValue value && value.TryGetValue<string>(out var text) &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private void SetChanged(DateTimeOffset? at)
    {
        lock (_gate) _changedAt = at;
        lock (StateGate)
        {
            try
            {
                var state = (File.Exists(_state) ? JsonNode.Parse(File.ReadAllText(_state)) as JsonObject : null) ?? new JsonObject();
                if (at is { } time) state[ChangedKey] = time.ToString("O", CultureInfo.InvariantCulture);
                else state.Remove(ChangedKey);
                HostFiles.Write(_state, state);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // The card stays in memory; a full disk must not stop Amberline.
                Console.Error.WriteLine($"Amberline could not save the address change: {ex.GetType().Name}");
            }
        }
    }

    private static readonly IResult Expired = HostMode.Page(
        $"<h1>The page expired</h1><p><a href={HostDashboard.PagePath}>Open the dashboard</a> and try again.</p>", StatusCodes.Status400BadRequest);
}
