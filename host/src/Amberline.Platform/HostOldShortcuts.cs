using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Amberline.Host;
using Microsoft.AspNetCore.Antiforgery;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL (batch 856, owner 17 November 2026: "Approve, build it", design <c>2026-11-17-batch-856-move-126-friends-design.md</c> Part 2;
/// retail moved its players to a new patch with its own patcher): the host's Move my 1.0.0.126 players here, and the stand-in on that line's
/// old friends' port. Only the 1.0.0.132 line's Program.cs makes one (with <c>PlatformLine.CopyLoginsFrom</c>); the 1.0.0.126 line has none.
/// <list type="bullet">
/// <item>The button, on Users beside Copy the logins and shown only while a trusted 1.0.0.126 install is found, runs the login copy (batch 830's,
/// <see cref="HostLoginCopy.CopyAsync"/>) and, unless the copy was refused, turns guiding on (<c>guideOldShortcuts</c> in this data folder's
/// <c>host\state.json</c>); Stop guiding old shortcuts turns it off.</item>
/// <item>The stand-in runs while this Platform runs, guiding is on, the install is trusted, the player tool it serves says it understands
/// <c>movedTo</c> (<c>capabilities.json</c> at the archive's root, <c>{"movedTo":1}</c>) and the old port is free. It answers HTTPS with that
/// install's own certificate on its friends' port (its <c>platform.json</c> <c>remotePort</c>, else <see cref="EarlierHome.RemotePort"/>):
/// <c>/bootstrap/client.json</c> (that server's manifest, the request's own host name as <c>host</c>, and <see cref="MovedTo"/> naming this
/// server) and <c>/bootstrap/companion.zip</c> (that install's archive); everything else is 404. Batch 856b review (17 November 2026): the
/// key and the archive are read only when this Platform starts with guiding on and when the host presses the button; that snapshot (bytes,
/// SHA-256, pin, the loaded certificate) stays in memory until Stop guiding or the Platform stops. The tries at each network test and every
/// <see cref="HostLoop.AddressEvery"/> only bind the port again with it: a file changed on disk counts from the next start or press.</item>
/// <item>Trust (design, Security; batch 830's rule; batch 857 on every route, the pointer's included): an install is trusted only while its
/// folders and, through the very handle each is read from, its files pass <see cref="CopyPlaces.OnlyYours"/>. One that does not is not locked yet:
/// the reason tells the host to update it to <see cref="EarlierHome.LocksFrom"/> and start it once, which locks it; this line never changes it.
/// On every route each file's handle must lead to the path asked for (no junction or symbolic link on the way) and the file must have no
/// second name (no hard link), checked before and after the read. The before-batch-637 data route names no install, so it is not used.
/// The key is opened read-only, never through <see cref="HostFiles.Certificate"/> (which would make a missing one), and only kept in memory;
/// the manifest's SHA-256 and the archive served come from one read.</item>
/// </list>
/// </summary>
public sealed class HostOldShortcuts : IAsyncDisposable
{
    public const string MovePath = HostInvite.UsersPath + "/move", StopPath = MovePath + "/stop";
    public const string StateKey = "guideOldShortcuts";
    // The player tool takes an archive of up to 512 MB (PlayerInstall.DownloadToolAsync); a key is a few kilobytes, a settings file a few more.
    private const long MostArchive = 512L << 20, MostKey = 64 << 10, MostSettings = 1 << 20;

    private readonly HostLoop _loop;
    private readonly EarlierHome _from;
    private readonly HostLoginCopy _copy;
    private readonly string _bind, _pin;
    private readonly int _remotePort;
    private readonly string _stateFile;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task _watch = Task.CompletedTask;
    private HostSteps? _steps;
    private volatile bool _guiding;
    // The snapshot (one read, at the start or the press; it owns the certificate), the running listener, what it serves, and the last try's
    // outcome (null: not tried yet). _first: the first try of this Platform reads the snapshot.
    private Snapshot? _snapshot;
    private WebApplication? _app;
    private volatile Served? _served;
    private volatile Outcome? _outcome;
    private bool _first = true;

    private sealed record Served(byte[] Archive, string Sha256, string Pin, int Port);
    private sealed record Outcome(bool Running, string? Reason, int Port);
    /// <summary>The install's key (loaded), what the stand-in serves, and the install's data folder (whose lease says that Amberline runs).</summary>
    private sealed record Snapshot(X509Certificate2 Certificate, Served Served, string Data);

    /// <param name="bind">This Platform's bind address (its friends' port's), which the stand-in listens on too.</param>
    /// <param name="remotePort">This Platform's own friends' port and <paramref name="pin"/> its certificate's, for <see cref="MovedTo"/>.</param>
    public HostOldShortcuts(HostLoop loop, EarlierHome from, HostLoginCopy copy, string bind, int remotePort, string pin)
    {
        _loop = loop;
        _from = from;
        _copy = copy;
        _bind = bind;
        _remotePort = remotePort;
        _pin = pin;
        _stateFile = HostFriendLight.StateFile(loop.Paths.Data);
        _guiding = ReadGuiding();
    }

    /// <summary>Whether the host turned guiding on (Move my 1.0.0.126 players here) and has not turned it off.</summary>
    public bool Guiding => _guiding;

    /// <summary>The port the router forwards while the stand-in runs (<see cref="HostDashboard"/>), else null.</summary>
    public UpnpPort? RouterPort => _outcome is { Running: true } running ? new UpnpPort("TCP", running.Port) : null;

    /// <summary>The old sign-in rule the Allow button adds while guiding is on (<see cref="HostFirewall.OldSignIn"/>), else null.</summary>
    public FirewallRule? Rule(string platformProgram) => _guiding ? HostFirewall.OldSignIn(platformProgram, _outcome?.Port ?? _from.RemotePort) : null;

    /// <summary>The network panel's reading of that rule (the dashboard sets it); null while it was not read.</summary>
    public Func<FirewallState?>? Firewall { get; set; }

    /// <summary>Called after a try changed whether the stand-in runs, and after the button: the dashboard tests the network again (UPnP).</summary>
    public Action? Changed { get; set; }

    /// <summary>DIAGNOSTICS: called with each file's path just before it is opened, so a check can swap the file in that moment.</summary>
    public Action<string>? BeforeOpen { get; set; }

    /// <summary>
    /// <see cref="MovedTo"/>'s player folder: this line's (<see cref="PlatformBootstrap.PlayerFolderName"/>, "Amberline 132 Player" on 1.0.0.132).
    /// DIAGNOSTICS: settable, so the end-to-end check names a folder the tool of the line it runs on accepts (a tool refuses its own line's).
    /// </summary>
    public string PlayerFolder { get; init; } = PlatformBootstrap.PlayerFolderName;

    /// <summary>
    /// The guiding line (design Part 2, step 6): null while guiding is off or before the first try; else whether the stand-in runs with its
    /// rule allowed, and the words of Status and Users.
    /// </summary>
    public (bool Running, string Text)? Line()
    {
        if (!_guiding || _outcome is not { } outcome) return null;
        if (outcome.Reason is { } reason) return (false, reason);
        // A missing rule changes only this line, never the firewall check (design, Ports); an unreadable firewall is that check's card.
        if (Firewall?.Invoke() is { } firewall && firewall is not (FirewallState.Allowed or FirewallState.Unreadable))
            return (false, "Allow it through Windows Firewall");
        return (true, $"Old {_from.Patch} shortcuts are guided here");
    }

    /// <summary>The routes and the watch; <paramref name="steps"/> names this server (<see cref="MovedTo"/>'s name, read per request).</summary>
    public void Map(WebApplication app, HostSteps? steps)
    {
        _steps = steps;
        app.Lifetime.ApplicationStarted.Register(() => _watch = WatchAsync());
        app.MapPost(MovePath, MoveAsync);
        app.MapPost(StopPath, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (await HostSteps.FormAsync(context, antiforgery) is null)
                return HostMode.Page("<h1>Stop guiding old shortcuts</h1>" + HostPages.Errors((HostSteps.Expired, null)) +
                    $"<div class=actions><a class=button href={HostInvite.UsersPath}>Back to Users</a></div>", StatusCodes.Status400BadRequest);
            if (!SetGuiding(false)) return Unsaved;
            await TryAgainAsync(context.RequestAborted);
            return Results.Redirect(HostInvite.UsersPath);
        });
    }

    /// <summary>Users' part: the button while a trusted install is found, and while guiding is on its line, the Allow button it may need, and Stop.</summary>
    internal string Offer(string token)
    {
        var found = Trusted();
        var line = Line();
        if (!found && !_guiding) return "";
        var patch = _from.Patch;
        var move = !found ? "" : $"""
            <form method=post action={MovePath}{HostPages.Wait("Copying the logins.")}>{token}
            {(_guiding ? "" : $"<p>Your Amberline for {patch} is on this PC. Its users' old shortcuts can lead them here. Each shortcut offers to set up this server, and they sign in with the same login.</p>")}
            <div class=actions><button type=submit class=secondary>Move my {patch} players here</button></div>
            <p class=tip>This first copies the logins that are not here yet. Your Amberline for {patch} stays installed. Keep it closed while this one runs.</p></form>
            """;
        var guiding = !_guiding ? "" :
            (line is { } shown ? $"<p class={(shown.Running ? "notice" : "warn")} role=status>{WebUtility.HtmlEncode(shown.Text)}</p>" : "") +
            (line?.Text == "Allow it through Windows Firewall"
                ? $"<form method=post action={HostDashboard.PagePath}/firewall{HostPages.Wait("Waiting for Windows. Choose Yes when Windows asks.")}>{token}<button type=submit class=secondary>Allow through Windows Firewall</button></form>"
                : "") +
            $"<form method=post action={StopPath}>{token}<button type=submit class=secondary>Stop guiding old shortcuts</button></form>";
        return $"<section aria-labelledby=old-shortcuts><h2 id=old-shortcuts>Old {patch} shortcuts</h2>{guiding}{move}</section>";
    }

    /// <summary>
    /// Move my 1.0.0.126 players here: the login copy, then guiding on, unless the copy was refused (that Amberline runs, others can change it,
    /// a link) or its form expired. Pressing it again copies the logins made there since.
    /// </summary>
    private async Task<IResult> MoveAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!Trusted())
            return HostMode.Page($"<h1>Move my {_from.Patch} players here</h1><p class=warn>Your Amberline for {_from.Patch} was not found.</p>" +
                $"<div class=actions><a class=button href={HostInvite.UsersPath}>Back to Users</a></div>", StatusCodes.Status404NotFound);
        var on = false;
        var page = await _copy.CopyAsync(context, antiforgery, setup: false, then: () =>
        {
            on = SetGuiding(true);
            return on ? $"<p class=notice role=status>Old {_from.Patch} shortcuts are guided to this server from now on. Users and the dashboard's Status say whether it works.</p>"
                : $"<p class=warn>{UnsavedText}</p>";
        });
        // A press reads the install's files afresh (the snapshot); the retries never do.
        if (on) await TryAgainAsync(context.RequestAborted, read: true);
        return page;
    }

    private const string UnsavedText = "Amberline could not save this. Try again.";

    private static readonly IResult Unsaved = HostMode.Page($"<h1>Not saved</h1><p class=warn>{UnsavedText}</p>" +
        $"<div class=actions><a class=button href={HostInvite.UsersPath}>Back to Users</a></div>", StatusCodes.Status503ServiceUnavailable);

    /// <summary>A try now, then the dashboard's network test (the router's port, the firewall line).</summary>
    private async Task TryAgainAsync(CancellationToken cancel, bool read = false)
    {
        await RefreshAsync(cancel, read);
        Changed?.Invoke();
    }

    private async Task WatchAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(_loop.AddressEvery);
            do
            {
                // A failed try leaves the stand-in off (RefreshAsync); the next period tries again.
                if (await RefreshAsync(_stop.Token)) Changed?.Invoke();
            }
            while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// One try. With guiding off the stand-in stops and its snapshot goes. With guiding on: the first try of this Platform, and a try with
    /// <paramref name="read"/> (the button), read the install's files into a new snapshot; every other try only binds the port again with the
    /// snapshot it has (a refused port, closed since). True when whether it runs, or its port, changed.
    /// </summary>
    public async Task<bool> RefreshAsync(CancellationToken cancel, bool read = false)
    {
        await _busy.WaitAsync(cancel);
        try
        {
            var before = _outcome;
            Outcome now;
            try { now = await TryAsync(read); }
            catch (Exception ex)
            {
                // Never a failed network test or page: the stand-in stays off without its snapshot until the next start or press.
                Console.Error.WriteLine($"Amberline's old-shortcut stand-in failed: {ex.GetType().Name}");
                await StopListenerAsync();
                DropSnapshot();
                now = new(false, NotFound, _from.RemotePort);
            }
            _outcome = now;
            return (before?.Running ?? false) != now.Running || now.Running && before!.Port != now.Port;
        }
        finally { _busy.Release(); }
    }

    private string NotFound => $"Your Amberline for {_from.Patch} was not found";

    /// <summary>Batch 857 (owner, 17 November 2026: that line locks itself): the reason while its install is not locked, which says what to do.</summary>
    private string NotLocked => $"Update your Amberline for {_from.Patch} to {_from.LocksFrom} or later and start it once";

    // Batch 857: set when a file's own security failed the check during the last read (Open), so the reason is NotLocked rather than NotFound.
    private volatile bool _notLocked;

    private async Task<Outcome> TryAsync(bool read)
    {
        var first = _first;
        _first = false;
        if (!_guiding)
        {
            await StopListenerAsync();
            DropSnapshot();
            return new(false, null, _from.RemotePort);
        }
        if (read || first)
        {
            await StopListenerAsync();
            DropSnapshot();
            var (snapshot, reason) = ReadSnapshot();
            if (snapshot is null) return reason!;
            _snapshot = snapshot;
        }
        if (_app is not null && _outcome is { Running: true } running) return running;
        // No snapshot: the reason of the read that failed stands until the next start or press.
        if (_snapshot is not { } current) return _outcome ?? new(false, NotFound, _from.RemotePort);
        var port = current.Served.Port;
        try
        {
            await StartListenerAsync(current.Certificate, port);
            _served = current.Served;
            return new(true, null, port);
        }
        catch (Exception ex) when (HostChecks.PortRefused(ex))
        {
            Console.WriteLine($"Port {port} is in use. Amberline tries again later to guide old {_from.Patch} shortcuts.");
            return new(false, HostLoginCopy.Leased(current.Data) ? $"Close your Amberline for {_from.Patch}" : $"Port {port} is in use by another program", port);
        }
    }

    /// <summary>
    /// The install's files, read once through checked handles: the archive's marker is checked before the key is imported, and the key is
    /// imported once per snapshot. Null with the reason when the install is not found or not trusted, or its tool is too old.
    /// </summary>
    private (Snapshot? Snapshot, Outcome? Reason) ReadSnapshot()
    {
        if (Install() is not { } install) return (null, new(false, NotFound, _from.RemotePort));
        if (!FoldersTrusted(install)) return (null, new(false, NotLocked, _from.RemotePort));
        _notLocked = false;
        try
        {
            if (ReadFiles(install) is not { } files) return (null, new(false, _notLocked ? NotLocked : NotFound, _from.RemotePort));
            if (!Capable(files.Archive)) return (null, new(false, $"Update your Amberline for {_from.Patch} to {_from.LocksFrom} or later", files.Port));
            var certificate = X509CertificateLoader.LoadPkcs12(files.Key, null);
            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                Console.Error.WriteLine($"The Amberline for {_from.Patch}'s certificate has no key.");
                return (null, new(false, NotFound, _from.RemotePort));
            }
            var served = new Served(files.Archive, Convert.ToHexString(SHA256.HashData(files.Archive)), certificate.GetCertHashString(HashAlgorithmName.SHA256), files.Port);
            return (new(certificate, served, Path.Combine(install.Folder, "data")), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            Console.Error.WriteLine($"Amberline could not read the Amberline for {_from.Patch} ({ex.GetType().Name}).");
            return (null, new(false, NotFound, _from.RemotePort));
        }
    }

    /// <summary>The snapshot goes, and with it the loaded key (the listener is stopped first).</summary>
    private void DropSnapshot()
    {
        _snapshot?.Certificate.Dispose();
        _snapshot = null;
    }

    private async Task StartListenerAsync(X509Certificate2 certificate, int port)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        // As the Platform's own (Program.RunAsync): ASP.NET Core's lines from Warning up. A refused port is this class's reason on Status, tried
        // again every few minutes, so the host's own report of it (a stack trace each time) stays out of the log. Batch 867: never the event log.
        builder.Logging.ConsoleOnly();
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Extensions.Hosting", LogLevel.Critical);
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = 16 * 1024;
            o.Limits.MaxConcurrentConnections = 64;
            o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            o.Listen(IPAddress.Parse(_bind), port, listen => listen.UseHttps(certificate));
        });
        // The friends' port's limits (Program.RequestPartition): the archive six times an hour per address, the rest 180 a minute.
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = 429;
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(Program.RequestPartition);
        });
        var app = builder.Build();
        app.UseRateLimiter();
        app.Run(ServeAsync);
        try { await app.StartAsync(); }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
        _app = app;
    }

    private async Task StopListenerAsync()
    {
        _served = null;
        if (_app is { } app)
        {
            _app = null;
            try { await app.StopAsync(); }
            finally { await app.DisposeAsync(); }
        }
    }

    /// <summary>
    /// The stand-in's one handler: GET <c>/bootstrap/client.json</c> (the old server's manifest with <c>movedTo</c>; its <c>host</c> is the name the
    /// request reached it by, so both released readers see no change, and a Host header that is no DNS name or IPv4 address gets none) and GET
    /// <c>/bootstrap/companion.zip</c>; 404 for everything else, as the old server's friends' port answers anything not on its list.
    /// </summary>
    private async Task ServeAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var served = _served;
        var name = context.Request.Host.Host;
        var result = served is null || !HttpMethods.IsGet(context.Request.Method) ? Results.NotFound()
            : context.Request.Path.Value switch
            {
                "/bootstrap/client.json" when PlatformBootstrap.IsHost(name) => PlatformBootstrap.Manifest(name, served.Port, served.Pin, served.Sha256, null,
                    new MovedTo(_remotePort, _pin, _steps?.ServerName ?? HostInvite.DefaultName, PlayerFolder)),
                "/bootstrap/companion.zip" => Results.File(served.Archive, "application/zip", "companion.zip", enableRangeProcessing: true),
                _ => Results.NotFound(),
            };
        await result.ExecuteAsync(context);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _watch;
        await _busy.WaitAsync();
        try
        {
            await StopListenerAsync();
            DropSnapshot();
        }
        finally { _busy.Release(); }
        _stop.Dispose();
    }

    // ---- The install and its files ----

    private sealed record InstallFolder(string Folder);

    /// <summary>
    /// The install as that line's own start takes it: the folder this user's pointer names (with its marker), else the default folder at the
    /// drive's root (with its marker). Null when neither is there. Batch 857: either is trusted only while it passes <see cref="FoldersTrusted"/>.
    /// </summary>
    private InstallFolder? Install()
    {
        var places = _loop.CopyFrom;
        if (places.LocalAppData is { } local && HostLoginCopy.Pointer(Path.Combine(local, _from.Name, "install.json"), _from.Marker) is { } pointed)
            return new(pointed);
        return places.Drive is { } drive && File.Exists(Path.Combine(drive, _from.Name, _from.Marker)) ? new(Path.Combine(drive, _from.Name)) : null;
    }

    private static string KeyPath(InstallFolder install) => Path.Combine(install.Folder, "data", "remote", "host.pfx");
    private static string ToolPath(InstallFolder install) => Path.Combine(install.Folder, "app", "bootstrap", "companion.zip");
    private static string SettingsPath(InstallFolder install) => Path.Combine(install.Folder, "data", "platform.json");

    /// <summary>Whether a trusted install is found now: its key and its tool open and pass every check (nothing is read).</summary>
    private bool Trusted()
    {
        if (Install() is not { } install || !FoldersTrusted(install)) return false;
        try
        {
            using var key = Open(KeyPath(install));
            using var tool = Open(ToolPath(install));
            return key is not null && tool is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private sealed record Files(byte[] Key, byte[] Archive, int Port);

    /// <summary>The key's and the tool's bytes and the port, each read through a checked handle; null when a check failed (the log says which part).</summary>
    private Files? ReadFiles(InstallFolder install)
    {
        // Its folders passed FoldersTrusted just before (ReadSnapshot), so a refusal there names its own reason.
        var key = ReadChecked(KeyPath(install), MostKey);
        var archive = key is null ? null : ReadChecked(ToolPath(install), MostArchive);
        if (key is null || archive is null) return null;
        var port = _from.RemotePort;
        if (File.Exists(SettingsPath(install)))
        {
            if (ReadChecked(SettingsPath(install), MostSettings) is not { } settings) return null;
            using var document = JsonDocument.Parse(settings);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("remotePort", out var value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var saved) && saved is >= 1 and <= 65535) port = saved;
        }
        return new(key, archive, port);
    }

    /// <summary>On every route (batch 857), the install's folders down to both files must be this user's (<see cref="CopyPlaces.OnlyYours"/>).</summary>
    private bool FoldersTrusted(InstallFolder install)
    {
        if (!OperatingSystem.IsWindows() || _loop.CopyFrom.OnlyYours is not { } yours) return false;
        foreach (var folder in new[] { "", "data", Path.Combine("data", "remote"), "app", Path.Combine("app", "bootstrap") })
            try
            {
                if (!yours(new DirectoryInfo(Path.Combine(install.Folder, folder)).GetAccessControl()))
                {
                    Console.Error.WriteLine($"Other people who use this PC can change the Amberline for {_from.Patch} in {install.Folder}.");
                    return false;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
        return true;
    }

    /// <summary>The file's bytes, read through a checked handle and checked again after the read; null when a check failed or it is too large.</summary>
    private byte[]? ReadChecked(string path, long most)
    {
        using var stream = Open(path);
        if (stream is null) return null;
        if (stream.Length > most)
        {
            Console.Error.WriteLine($"{Path.GetFileName(path)} of the Amberline for {_from.Patch} is larger than {most} bytes.");
            return null;
        }
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return Checked(stream, path) ? bytes : null;
    }

    /// <summary>
    /// The file opened for reading while nobody may write or delete it, and checked on that very handle (design, Security): it leads to
    /// <paramref name="path"/> itself and has one name (<see cref="Checked"/>), and (batch 857: on every route) its security passes
    /// <see cref="CopyPlaces.OnlyYours"/>. Null, with the reason in the log, when a check fails or the file is missing.
    /// </summary>
    private FileStream? Open(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        BeforeOpen?.Invoke(path);
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
        try
        {
            if (Checked(stream, path))
            {
                if (_loop.CopyFrom.OnlyYours is { } yours && yours(stream.GetAccessControl())) return stream;
                _notLocked = true;
                Console.Error.WriteLine($"Other people who use this PC can change {Path.GetFileName(path)} of the Amberline for {_from.Patch}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Amberline could not check {Path.GetFileName(path)} of the Amberline for {_from.Patch} ({ex.GetType().Name}).");
        }
        stream.Dispose();
        return null;
    }

    /// <summary>
    /// The handle leads to <paramref name="path"/> itself (Windows' final name for it, so a junction or symbolic link anywhere on the way shows)
    /// and the file has a single name (no hard link). ponytail: a path written with 8.3 short names, or through a subst drive, is refused too;
    /// expand it first if a real install ever needs it.
    /// </summary>
    private bool Checked(FileStream stream, string path)
    {
        var final = Folders.InstallLock.FinalPath(stream.SafeFileHandle);
        var links = Folders.InstallLock.Links(stream.SafeFileHandle);
        if (final is not null && string.Equals(final, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && links == 1) return true;
        Console.Error.WriteLine($"{Path.GetFileName(path)} of the Amberline for {_from.Patch} is reached through a link, so Amberline does not use it.");
        return false;
    }

    /// <summary>Interface 2 of batch 856: the archive's root holds <c>capabilities.json</c> whose <c>movedTo</c> is 1 (read from the bytes served).</summary>
    internal static bool Capable(byte[] archive)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read);
            if (zip.GetEntry("capabilities.json") is not { } entry) return false;
            // A small file: at most 4 KB is read, whatever the entry claims.
            var buffer = new byte[4097];
            var total = 0;
            using (var stream = entry.Open())
                for (int read; total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0;) total += read;
            if (total == buffer.Length) return false;
            using var document = JsonDocument.Parse(new MemoryStream(buffer, 0, total));
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("movedTo", out var value) &&
                value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var level) && level == 1;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException) { return false; }
    }

    // ---- Guiding on or off, in host\state.json ----

    private bool ReadGuiding()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(_stateFile)) is JsonObject state && state[StateKey] is JsonValue value &&
                value.TryGetValue<bool>(out var on) && on;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    /// <summary>Saves guiding on or off, merged into <c>host\state.json</c> under its writers' lock; false when it could not be saved (it stays as it was).</summary>
    private bool SetGuiding(bool on)
    {
        lock (HostAttention.StateGate)
        {
            try
            {
                var state = (File.Exists(_stateFile) ? JsonNode.Parse(File.ReadAllText(_stateFile)) as JsonObject : null) ?? new JsonObject();
                if (on) state[StateKey] = true;
                else state.Remove(StateKey);
                HostFiles.Write(_stateFile, state);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Console.Error.WriteLine($"Amberline could not save the guiding of old shortcuts: {ex.GetType().Name}");
                return false;
            }
        }
        _guiding = on;
        return true;
    }
}
