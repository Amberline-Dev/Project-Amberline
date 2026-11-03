using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Amberline.Host;
using Microsoft.AspNetCore.DataProtection;

namespace Amberline.Platform;

/// <summary>
/// The folders <c>--home</c> derives. Data: <c>AMBERLINE_PLATFORM_HOME</c> when set; else, for a program in a one-folder install
/// (batch 637: <c>&lt;install&gt;\app\platform</c> with the install's marker beside <c>app</c>), <c>&lt;install&gt;\data</c>; else
/// <c>Amberline\data</c> in the user's local application data (the installs before batch 637). Either way it survives updating the
/// program. The match server and the friends' <c>companion.zip</c> sit beside the Platform's own folder (<c>..\server</c>,
/// <c>..\bootstrap</c>), so <c>platform.json</c> holds no program path.
/// </summary>
public sealed record HostPaths(string Data, string App)
{
    public static HostPaths Resolve(string? platformHome, string localAppData, string appFolder) =>
        new(Path.GetFullPath(!string.IsNullOrWhiteSpace(platformHome) ? platformHome
                : InstallFolder(appFolder) is { } install ? Path.Combine(install, "data") : Path.Combine(localAppData, "Amberline", "data")),
            Path.GetFullPath(appFolder));

    /// <summary>The one-folder install a Platform folder (<c>app\platform</c>) belongs to, or null when it runs from anywhere else.</summary>
    public static string? InstallFolder(string platformFolder)
    {
        var install = Path.GetFullPath(Path.Combine(platformFolder, "..", ".."));
        return File.Exists(Path.Combine(install, Folders.FolderMove.InstallMarker)) ? Path.TrimEndingDirectorySeparator(install) : null;
    }

    public string Settings => Path.Combine(Data, "platform.json");
    public string Logs => Path.Combine(Data, "logs");
    public string ServerExecutable =>
        Path.GetFullPath(Path.Combine(App, "..", "server", OperatingSystem.IsWindows() ? "Amberline.Server.exe" : "Amberline.Server"));
    public string Bootstrap => Path.GetFullPath(Path.Combine(App, "..", "bootstrap"));
    /// <summary>The page's form-key ring, so nothing is written outside the data folder and a restart keeps forms valid.</summary>
    public string Keys => Path.Combine(Data, "host", "keys");

    /// <summary>The start's problem while <c>platform.json</c> names no folders yet (the folder step's own rows say more).</summary>
    public const string NoFolders = "Choose the game folder and the AIR client folder.";

    /// <summary>
    /// Home settings need both chosen folders; an empty <c>serverExecutable</c> means the one beside this program
    /// (<see cref="HostSettings.Validate"/> still checks that it exists).
    /// </summary>
    public PlatformSettings Derive(PlatformSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DataRoot) || string.IsNullOrWhiteSpace(settings.AirRoot))
            throw new ArgumentException(NoFolders);
        if (HostFolders.Gone(settings.DataRoot, settings.AirRoot) is { } gone) throw new ArgumentException(gone);
        if (!string.IsNullOrWhiteSpace(settings.ServerExecutable)) return settings;
        if (!File.Exists(ServerExecutable)) throw new ArgumentException(HostChecks.MissingPart(Path.Combine(App, ".."), ServerExecutable));
        return settings with { ServerExecutable = ServerExecutable };
    }
}

/// <summary>
/// One <c>--home</c> process: its folders, the browser opener and the visible window, and the loop's control. Setup finishing (and
/// later Restart or a settings change) stops the running <see cref="WebApplication"/>, and the next start runs in a new process in
/// the same window (<see cref="NextProcess"/>); Stop, Ctrl+C or closing the window leaves the loop.
/// </summary>
public sealed class HostLoop(HostPaths paths, Action<string> openBrowser, TextWriter screen)
{
    private readonly Lock _gate = new();
    private IHostApplicationLifetime? _running;
    private WebApplication? _app;
    private bool _restart, _stopped;

    public HostPaths Paths => paths;
    public TextWriter Screen => screen;

    /// <summary>Where the setup page looks for the game and AIR folders (<see cref="HostFolders"/>); by default nowhere.</summary>
    public FolderSetup Folders { get; init; } = new();

    /// <summary>Play on this PC (<see cref="HostPlay"/>); by default it starts nothing, so a check never opens a client.</summary>
    public HostPlay Play { get; init; } = new(_ => null);

    /// <summary>The page's launch-code key and signed-in browsers, one per process: a Restart keeps the host signed in.</summary>
    public HostSessions Sessions { get; } = new(HostSessions.KeyFile(paths.Data));

    /// <summary>The router, firewall and route the dashboard tests; null tests nothing (only <see cref="HostMode.MainAsync"/> passes the real one).</summary>
    public HostNetwork? Network { get; init; }

    /// <summary>The dashboard's friend light, kept across restarts.</summary>
    public HostFriendLight Friend { get; } = new(paths.Data);

    /// <summary>Where the address step asks for the host's address (<see cref="HostSteps"/>); only the real start goes online.</summary>
    public HostAddressSources Addresses { get; set; } = HostAddressSources.Offline;

    /// <summary>How often the dashboard asks the router again for the address (design section 5.6: every 5 minutes).</summary>
    public TimeSpan AddressEvery { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Where Uninstall Amberline removes the icon and the pointer and writes its cleanup (<see cref="HostUninstall"/>). By default
    /// nowhere, so a check's loop never touches a real desktop, profile or temp folder; only <see cref="HostMode.MainAsync"/> passes this PC's.
    /// </summary>
    public UninstallPlaces Uninstall { get; set; } = UninstallPlaces.Nowhere;

    /// <summary>
    /// Where Updates asks GitHub and downloads, and how Update now starts a release (<see cref="HostReleases"/>). By default nowhere and
    /// nothing, so a check's loop never reaches GitHub or starts a program; only <see cref="HostMode.MainAsync"/> passes the live one.
    /// </summary>
    public HostReleases Releases { get; set; } = HostReleases.Offline;

    /// <summary>The cleanup Uninstall Amberline handed the install folder to; <see cref="HostMode.MainAsync"/> starts it once the loop has ended.</summary>
    public ProcessStartInfo? Cleanup { get; internal set; }

    /// <summary>DIAGNOSTICS: the routes of the mode running now, so a check can walk every one of them.</summary>
    public Endpoint[] Routes()
    {
        lock (_gate) return _app is null ? [] : ((IEndpointRouteBuilder)_app).DataSources.SelectMany(source => source.Endpoints).ToArray();
    }

    /// <summary>Why the last start fell back to setup mode (shown on the setup page), or null.</summary>
    public string? Problem { get; internal set; }

    /// <summary>How many modes have started listening in this process (setup and full alike).</summary>
    public int Starts { get; private set; }

    /// <summary>
    /// What a Restart starts: this program with its arguments (<see cref="HostMode.MainAsync"/>). The game data, the champion list and
    /// the rune catalogs are read once per process, so the next start needs a fresh one to read the folders just saved (batch 636).
    /// DIAGNOSTICS: null, the checks' default, builds the next start in this process.
    /// </summary>
    public ProcessStartInfo? NextProcess { get; init; }

    /// <summary>A Restart started this process: the window already shows the banner and the browser the page.</summary>
    public bool Restarted { get; init; }

    /// <summary>This process handed the window to <see cref="NextProcess"/>; its exit code is the window's.</summary>
    public bool HandedOver { get; internal set; }

    /// <summary>Where a restarting page sends the browser: <c>/open</c> with a fresh code, which the next process takes too.</summary>
    public string ReopenPath => "/open?c=" + Sessions.Code(DateTimeOffset.UtcNow);

    /// <summary>The same reopen, landing on a page of <see cref="HostGate.LandingFor"/>'s list (batch 649: Settings after a folder change).</summary>
    public string ReopenPathTo(string to) => ReopenPath + "&to=" + to;

    public void Restart() { lock (_gate) { _restart = true; _running?.StopApplication(); } }
    public void Stop() { lock (_gate) { _stopped = true; _running?.StopApplication(); } }

    /// <summary>The page's entry with a fresh launch code (<see cref="HostSessions"/>): it works once, within 2 minutes.</summary>
    public string PageUrl(int port) => $"http://127.0.0.1:{port}/open?c={Sessions.Code(DateTimeOffset.UtcNow)}";

    public void OpenPage(int port) => openBrowser(PageUrl(port));

    /// <summary>
    /// Runs one mode until it stops; true when the loop builds the next one. The first start of a window shows the banner and opens
    /// the page (a <see cref="Restarted"/> process's never does).
    /// </summary>
    public async Task<bool> RunAsync(WebApplication app, int port)
    {
        lock (_gate)
        {
            if (_stopped) return false;
            _restart = false;
            _running = app.Lifetime;
            _app = app;
        }
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            lock (_gate) { if (++Starts > 1 || Restarted) return; }
            screen.WriteLine("Amberline is running. Closing this window stops it and any game in progress.");
            screen.WriteLine("Its page opens in your browser. If it does not, open this link within 2 minutes:");
            screen.WriteLine(PageUrl(port));
            OpenPage(port);
        });
        await app.RunAsync();
        lock (_gate)
        {
            _running = null;
            _app = null;
            return _restart && !_stopped;
        }
    }
}

/// <summary>
/// NOT RETAIL: the home host's start (retail ran its servers in Riot's data centres). <c>--home</c> keeps its data in the user's
/// profile, answers only on loopback until the game folders are chosen, finds an Amberline already running through <c>/health</c>,
/// opens the host's page in the browser and keeps its log in a file, so the window shows only the banner and fatal errors.
/// </summary>
public static class HostMode
{
    public const int DefaultPort = 8390;
    /// <summary>Set to 1 for the process a Restart starts (<see cref="HostLoop.Restarted"/>).</summary>
    public const string RestartedVariable = "AMBERLINE_PLATFORM_RESTARTED";
    private const int KeptLogs = 14;

    /// <summary>The <c>--home</c> start. <paramref name="options"/> (after <c>--home</c>) are START-HERE's install request (<see cref="ParseInstall"/>).</summary>
    public static async Task<int> MainAsync(string[] options)
    {
        var screen = Console.Out;
        try { Console.Title = "Amberline"; } catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { }
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var paths = HostPaths.Resolve(Environment.GetEnvironmentVariable("AMBERLINE_PLATFORM_HOME"), localAppData, AppContext.BaseDirectory);
        // Uninstall Amberline.cmd in the install folder: the uninstall of Settings, asked in this window.
        if (options is ["--uninstall"])
        {
            var (code, cleanup) = await HostUninstall.ConsoleAsync(paths, UninstallPlaces.ThisPc(), HostNetwork.Windows, screen, Console.ReadLine);
            WaitForEnter(screen);
            HostUninstall.Start(cleanup);
            return code;
        }
        var request = ParseInstall(options);
        // A start from an unpacked release installs or updates the installed copy and starts that one instead.
        var install = request is { } asked ? HostInstall.ForRelease(paths, localAppData, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            screen, asked.Folder, asked.Clients, asked.Confirmed) : null;
        var exit = install is null && options.Length > 0
            ? Refuse(screen, "Amberline does not know these options. Start it from its desktop icon or Start Amberline.cmd.")
            : install is { AsksFolder: true } && !install.AskFolder(Console.ReadLine) ? Refuse(screen, "Amberline was not installed.", 1)
            : install?.Check() is { } refused ? Refuse(screen, refused)
            : await RunHomeAsync(install is null ? paths : install.Target, install, screen);
        return exit;
    }

    /// <summary>The start once the data folder is known: the log, then the install or the loop.</summary>
    private static async Task<int> RunHomeAsync(HostPaths paths, HostInstall? install, TextWriter screen)
    {
        // PlatformContent reads the data folder from this variable on first use; a Restart's new process and the installed
        // Platform an install starts inherit it.
        Environment.SetEnvironmentVariable("AMBERLINE_PLATFORM_HOME", paths.Data);
        var restarted = Environment.GetEnvironmentVariable(RestartedVariable) == "1";
        // The match servers and the companion that this process starts are not restarts.
        Environment.SetEnvironmentVariable(RestartedVariable, null);
        // Never disposed: background writers may log until the process ends; every line is flushed.
        var log = OpenLog(paths.Logs, DateTime.Now);
        Console.SetOut(log);
        Console.SetError(log);
        HostLoop? loop = null;
        var exit = install is not null ? await install.RunAsync()
            : await RunAsync(loop = new HostLoop(paths, OpenBrowser, screen)
            {
                Folders = FolderSetup.ThisPc(), Addresses = HostAddressSources.Live, Play = new(HostPlay.Start), Network = HostNetwork.Windows,
                NextProcess = ThisProgram(), Restarted = restarted, Uninstall = UninstallPlaces.ThisPc(), Releases = HostReleases.Live,
            });
        // A new process that a Restart started asked for Enter itself.
        if (exit != 0 && loop?.HandedOver != true) WaitForEnter(screen);
        // After Uninstall Amberline: the cleanup deletes the folder once this process has ended.
        HostUninstall.Start(loop?.Cleanup);
        return exit;
    }

    private static int Refuse(TextWriter screen, string message, int exit = 2)
    {
        screen.WriteLine(message);
        WaitForEnter(screen);
        return exit;
    }

    /// <summary>The window's end, in the word list's words (batch 653).</summary>
    public const string CloseLine = "Press Enter to close.";

    private static void WaitForEnter(TextWriter screen)
    {
        if (Console.IsInputRedirected) return;
        screen.WriteLine(CloseLine);
        Console.ReadLine();
    }

    /// <summary>
    /// START-HERE's install request after <c>--home</c> (batch 637): <c>--install-to &lt;folder&gt;</c>, <c>--game &lt;folder&gt;</c>,
    /// <c>--air &lt;folder&gt;</c> and <c>--delete-originals</c>, each optional; batch 666, <see cref="HostInstall.ConfirmedOption"/> (only
    /// Update now passes it: the host confirmed on the page). Null for anything else.
    /// </summary>
    public static (string? Folder, HostClients? Clients, bool Confirmed)? ParseInstall(IReadOnlyList<string> options)
    {
        string? folder = null, game = null, air = null;
        var (delete, confirmed) = (false, false);
        for (var index = 0; index < options.Count; index++)
            switch (options[index])
            {
                case "--install-to" when index + 1 < options.Count: folder = options[++index]; break;
                case "--game" when index + 1 < options.Count: game = options[++index]; break;
                case "--air" when index + 1 < options.Count: air = options[++index]; break;
                case "--delete-originals": delete = true; break;
                case HostInstall.ConfirmedOption: confirmed = true; break;
                default: return null;
            }
        return (folder, game is null && air is null ? null : new HostClients(game, air, delete), confirmed);
    }

    public static async Task<int> RunAsync(HostLoop loop)
    {
        try
        {
            Directory.CreateDirectory(loop.Paths.Data);
            var port = ReadPort(loop.Paths.Settings);
            switch (await ProbeAsync(port))
            {
                case true:
                    loop.Screen.WriteLine("Amberline is already running. Its page opens in your browser.");
                    loop.OpenPage(port);
                    return 0;
                case false:
                    return Fail(loop, HostChecks.PortInUse(port));
            }
            // Settings from a newer Amberline are left exactly as they are: no mode starts, nothing is saved.
            if (HostChecks.NewerData(loop.Paths.Settings) is { } newer) return Fail(loop, newer);
            while (true)
            {
                PlatformSettings? settings = null;
                HostSettings? host = null;
                try
                {
                    (settings, host) = Program.LoadSettings(loop.Paths.Settings, loop.Paths);
                    loop.Problem = null;
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or JsonException or InvalidDataException)
                {
                    loop.Problem = File.Exists(loop.Paths.Settings) ? ex.Message : null;
                    Console.WriteLine($"Amberline setup mode: {ex.Message}");
                }
                var next = settings is null
                    ? await RunSetupAsync(loop, ReadPort(loop.Paths.Settings))
                    : await Program.RunAsync(settings, host!, loop.Paths.Data, loop);
                if (!next) return 0;
                if (loop.NextProcess is { } start) return await RunNextAsync(loop, start);
            }
        }
        catch (Exception ex) when (HostChecks.PortRefused(ex))
        {
            // The failed start's listeners are closed by now, so the port still taken is another program's.
            Console.Error.WriteLine($"A listening port was refused: {ex.Message}");
            return Fail(loop, HostChecks.PortInUse(HostChecks.BusyPort(ReadOrDefault(loop.Paths.Settings))));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or JsonException)
        {
            return Fail(loop, ex.Message);
        }
    }

    /// <summary>
    /// What answers on the page's port: null when nothing listens, true for an Amberline (set up or not), false for anything else.
    /// </summary>
    public static async Task<bool?> ProbeAsync(int port)
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/health");
            using var health = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return response.IsSuccessStatusCode && health.RootElement.TryGetProperty("service", out var service) &&
                service.GetString() == "Amberline.AirPlatform" &&
                health.RootElement.TryGetProperty("mode", out var mode) && mode.GetString() == "home";
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConnectionError) { return null; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        { return false; }
    }

    /// <summary>
    /// The next start in a new process in this window (<see cref="HostLoop.NextProcess"/>); this one waits and ends with its exit code.
    /// Its ports, the account store's lease and its matches are closed by now, so the new process's single-instance check passes.
    /// </summary>
    private static async Task<int> RunNextAsync(HostLoop loop, ProcessStartInfo start)
    {
        start.Environment[RestartedVariable] = "1";
        // Ctrl+C reaches both processes: the new one stops by itself, and its exit code stays the window's.
        ConsoleCancelEventHandler wait = (_, stop) => stop.Cancel = true;
        Console.CancelKeyPress += wait;
        try
        {
            using var process = Process.Start(start)!;
            loop.HandedOver = true;
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Win32Exception ex) { return Fail(loop, $"Amberline could not start again ({ex.Message}). Start it from its desktop icon."); }
        finally { Console.CancelKeyPress -= wait; }
    }

    /// <summary>This program with its own arguments, sharing this window.</summary>
    private static ProcessStartInfo ThisProgram()
    {
        // The first argument is the program's own library: the arguments are the rest, and dotnet itself needs the library too.
        var arguments = Environment.GetCommandLineArgs();
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(arguments[0]);
        foreach (var argument in arguments[1..]) start.ArgumentList.Add(argument);
        return start;
    }

    /// <summary>The page's port from <c>platform.json</c>, or 8390 while there is no readable one.</summary>
    public static int ReadPort(string settingsFile)
    {
        try { return ExistingSettings(settingsFile).HttpPort is var port and >= 1 and <= 65535 ? port : DefaultPort; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return DefaultPort; }
    }

    private static PlatformSettings ExistingSettings(string settingsFile) =>
        JsonSerializer.Deserialize<PlatformSettings>(File.ReadAllText(settingsFile), HostFiles.Json) ?? new PlatformSettings(null!, null!);

    /// <summary>
    /// Setup mode: the loopback listener alone (no RTMP, XMPP, public HTTPS or games) serving only <c>/health</c>, <c>/open</c>,
    /// <c>/setup</c> and the name step (<see cref="HostSteps"/>). A form that names both folders is saved to <c>platform.json</c>
    /// and the loop starts the full Platform.
    /// </summary>
    private static async Task<bool> RunSetupAsync(HostLoop loop, int port)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        // As the full Platform's (Program.RunAsync): ASP.NET Core's own lines from Warning up.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Antiforgery", LogLevel.Error);
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Limits.MaxRequestBodySize = 16 * 1024;
            o.Limits.MaxConcurrentConnections = 64;
            o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            o.Listen(IPAddress.Loopback, port);
        });
        builder.Services.AddAntiforgery(o => o.Cookie.Name = "Amberline.Forms");
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(loop.Paths.Keys));
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = HostPages.Policy;
            if (!PlatformManagement.IsLocal(context, port)) { context.Response.StatusCode = 404; return; }
            await next(context);
        });
        app.MapGet("/health", () => Results.Json(new { status = "setup", service = "Amberline.AirPlatform", protocol = 1, mode = "home" }));
        HostGate.Use(app, port, loop.Sessions, HostSteps.Start);
        var steps = HostSteps.Map(app, loop, null);
        HostPages.Use(app, loop, () => steps.ServerName, nav: false);
        HostFolders.MapSetup(app, loop);
        HostInstall.MapSetupStop(app, loop);
        return await loop.RunAsync(app, port);
    }

    /// <summary>
    /// <c>platform.json</c> for a page to show, or the defaults when it cannot be read just now. Never the base of a save: that is
    /// <see cref="Change"/>, which would otherwise write these defaults over the other fields (batch 657).
    /// </summary>
    internal static PlatformSettings ReadOrDefault(string settingsFile)
    {
        try { return ExistingSettings(settingsFile); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new PlatformSettings(null!, null!); }
    }

    /// <summary>
    /// <c>platform.json</c> for a decision that must not run on defaults, read as <see cref="Change"/> reads it (tried again for up
    /// to 2 seconds, then <see cref="SettingsUnreadableException"/>). Friends uses it to keep the host's own login off its list.
    /// </summary>
    internal static PlatformSettings ReadStrict(string settingsFile)
    {
        PlatformSettings read = null!;
        Change(settingsFile, saved => read = saved);
        return read;
    }

    /// <summary>The page's words when a save could not read the settings it changes (<see cref="SettingsUnreadableException"/>).</summary>
    internal const string Unreadable = "Amberline could not read its settings. Try again.";

    private static readonly Lock SettingsGate = new();

    /// <summary>
    /// Every save of <c>platform.json</c>: read, changed by <paramref name="change"/> and written back, one save at a time. Only a
    /// missing file starts from the defaults. A read refused for a moment (a writer replacing the file, a virus scanner holding it)
    /// or a damaged file is tried again every 50 ms for up to 2 seconds, as <see cref="HostFiles.Write"/> tries its replace; after
    /// that it throws <see cref="SettingsUnreadableException"/> and nothing is written, so a failed read never erases the server
    /// name, the host's login, the address or the folders (batch 657). A change that returns the settings it was given writes
    /// nothing; the result says whether it wrote.
    /// </summary>
    internal static bool Change(string settingsFile, Func<PlatformSettings, PlatformSettings> change)
    {
        lock (SettingsGate)
        {
            PlatformSettings saved;
            for (var attempt = 1; ; attempt++)
                try
                {
                    saved = ExistingSettings(settingsFile);
                    break;
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    saved = new PlatformSettings(null!, null!);
                    break;
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    if (attempt == 40) throw new SettingsUnreadableException(ex);
                    Thread.Sleep(50);
                }
            var changed = change(saved);
            if (ReferenceEquals(changed, saved)) return false;
            HostFiles.Write(settingsFile, changed);
            return true;
        }
    }

    /// <summary>
    /// A host page in the shell (<see cref="HostPages"/>); <paramref name="refresh"/> is where it goes by itself after a few seconds,
    /// <paramref name="step"/> a first-run step's number (1 to <see cref="HostPages.Steps"/>), and <paramref name="nav"/> false for a
    /// page whose links would not answer (stopped, restarting).
    /// </summary>
    internal static IResult Page(string content, int status = StatusCodes.Status200OK, string? refresh = null, int step = 0, bool nav = true) =>
        HostPages.Page(content, status, refresh, step, nav);

    private static void OpenBrowser(string url)
    {
        // The banner names the address, so a browser that does not start is only logged.
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { Console.Error.WriteLine($"The browser did not open: {ex.Message}"); }
    }

    private static int Fail(HostLoop loop, string message)
    {
        loop.Screen.WriteLine(message);
        Console.Error.WriteLine(message);
        return 2;
    }

    /// <summary>
    /// <c>logs\amberline-&lt;date&gt;.log</c> of the day the process started, appended to; only the newest 14 such files are kept.
    /// ponytail: a process running past midnight keeps writing its start day's file; split by write time if days-long runs need it.
    /// </summary>
    public static TextWriter OpenLog(string folder, DateTime now)
    {
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.GetFiles(folder, "amberline-*.log").Order(StringComparer.Ordinal).SkipLast(KeptLogs - 1))
            try { File.Delete(old); } catch (IOException) { }
        var path = Path.Combine(folder, $"amberline-{now:yyyy-MM-dd}.log");
        return TextWriter.Synchronized(new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true });
    }
}

/// <summary>
/// <see cref="HostMode.Change"/> could not read <c>platform.json</c> and wrote nothing; a host page answers with
/// <see cref="HostMode.Unreadable"/> (<see cref="HostPages.Use"/>).
/// </summary>
public sealed class SettingsUnreadableException(Exception inner) : IOException(HostMode.Unreadable, inner);
