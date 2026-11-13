using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Amberline.AirCompanion;

public static class Program
{
    private static readonly TimeSpan PlatformStartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PlatformHealthTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PlatformProbeInterval = TimeSpan.FromMilliseconds(200);
    private const string ExpectedAirClientIdentity = "1.45.11_10_04_15_28";

    public static int Main(string[] args)
    {
        try { return Run(args).GetAwaiter().GetResult(); }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or TimeoutException or JsonException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    public static async Task<int> Run(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            PrintUsage();
            return 0;
        }
        // NOT RETAIL: the player's bring-your-own-clients mode (PlayerMode.cs), started by Play.cmd.
        if (args.Contains("--play", StringComparer.OrdinalIgnoreCase))
            return await PlayerMode.RunAsync(args);
        // NOT RETAIL: the player's installer (started by Play.cmd) and the windowless desktop shortcut it creates.
        if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
            return PlayerInstallWindow.Run(args);
        if (args.Contains("--shortcut", StringComparer.OrdinalIgnoreCase))
            return await PlayerMode.RunShortcutAsync(args);
        // NOT RETAIL: removing it again (Uninstall.cmd in the install folder, or Windows' "Apps" list).
        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
            return PlayerInstallWindow.RunUninstall();
        if (args.Contains("--prepare-only", StringComparer.OrdinalIgnoreCase))
        {
            var source = Required(args, "--source");
            var destination = Required(args, "--destination");
            AirOverlay.Prepare(new AirOverlayOptions(source, destination, Option(args, "--platform-url") ?? "http://127.0.0.1:8390",
                ParsePort(Option(args, "--maestro-port"))));
            Console.WriteLine($"Prepared disposable AIR copy: {Path.GetFullPath(destination)}");
            return 0;
        }
        if (args.Contains("--fonts-only", StringComparer.OrdinalIgnoreCase))
        {
            // DIAGNOSTICS: test tooling (tools/air-page-shots.ps1): the Friz host of --desktop alone, for a throwaway AIR copy that
            // runs without a game or Maestro. --cache-dir keeps the converted faces out of the operator's LocalAppData cache.
            // Runs until the caller stops it; exit 2 when the port is taken.
            await using var fontHost = await FrizFontServer.StartAsync(Path.GetFullPath(Required(args, "--air-root")), Console.WriteLine,
                cacheDirectory: Option(args, "--cache-dir"));
            if (fontHost.ListeningPort == 0) return 2;
            await Task.Delay(Timeout.Infinite);
            return 0;
        }
        if (args.Contains("--register", StringComparer.OrdinalIgnoreCase))
        {
            var platform = AirOverlay.ParsePlatformUrl(Option(args, "--platform-url") ?? "http://127.0.0.1:8390");
            Process.Start(new ProcessStartInfo(new Uri(platform, "register").AbsoluteUri) { UseShellExecute = true });
            return 0;
        }
        if (args.Contains("--remote-register", StringComparer.OrdinalIgnoreCase))
        {
            // Playtest B3 #11, 26 Sept 2026: public registration is off; the host creates every account.
            Console.Error.WriteLine("Registration is done by the host. Ask the host for your username and password.");
            return 1;
        }
#if AMBERLINE_ARCHIVE_INSTALLER
        // NOT RETAIL: the friend launcher's client-files setup. Compiled only while ClientFilesInstaller.cs exists (see the csproj).
        if (args.Contains("--install-files", StringComparer.OrdinalIgnoreCase))
        {
            // DIAGNOSTICS: --archive-base-url points both downloads at a local test server; the pinned sizes and hashes still apply.
            var archiveBase = Option(args, "--archive-base-url") is { } url ? new Uri(url.TrimEnd('/') + "/") : null;
            return await ClientFilesInstaller.RunAsync(Required(args, "--root"), archiveBase, CancellationToken.None);
        }
#endif
        if (args.Contains("--manage", StringComparer.OrdinalIgnoreCase))
            return await RunManageAsync(Required(args, "--stage-root"));
        if (args.Contains("--desktop", StringComparer.OrdinalIgnoreCase))
            return await RunDesktopAsync(Required(args, "--stage-root"), ParsePort(Option(args, "--maestro-port")),
                Option(args, "--remote-invitation"));
        if (!args.Contains("--start", StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Choose --play, --prepare-only, --start, --desktop, --manage or --register.");

        var airRoot = Path.GetFullPath(Required(args, "--air-root"));
        var stockExecutable = Path.GetFullPath(Required(args, "--stock-executable"));
        var airStart = CreateAirStartInfo(airRoot);
        PrepareGameFolder(stockExecutable);
        await using var fonts = await FrizFontServer.StartAsync(airRoot, Console.WriteLine);
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler? cancel = null;
        cancel = (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            await using var maestro = CreateMaestro(ParsePort(Option(args, "--maestro-port")), stockExecutable);
            await maestro.StartAsync(stop.Token);
            return await RunAirAsync(maestro, airStart, stop.Token);
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    /// <param name="LauncherRoot">NOT RETAIL: the folder of the player's or host's 2011 launcher files (the classic start, ClassicStart); null for
    /// a stage that never takes the classic start.</param>
    public sealed record DesktopStage(string Root, string AirRoot, string StockExecutable,
        string PlatformExecutable, string PlatformData, int HttpPort, int MaestroPort,
        AirRemoteInvitation? RemoteInvitation = null, string? AirSource = null, string? GameHost = null, string? LauncherRoot = null);

    /// <summary>
    /// The desktop launch; <paramref name="clientShown"/> runs once AIR's window shows (or AIR ends first): the shortcut's start window closes then.
    /// <paramref name="askClassic"/> asks after a classic start that never reached AIR (null: open the classic launcher again);
    /// <paramref name="notice"/> shows the fallback's one line (null: the log line only, as for the host app's Play).
    /// </summary>
    internal static async Task<int> RunDesktopAsync(string stageRoot, int requestedMaestroPort, string? remoteInvitationPath, Action? clientShown = null,
        Func<bool>? askClassic = null, Action<string>? notice = null)
    {
        var stage = LoadDesktopStage(stageRoot, requestedMaestroPort, remoteInvitationPath);
        if (string.IsNullOrWhiteSpace(stage.StockExecutable))
            throw new ArgumentException("The staged remote client has no stock executable. Select the game executable in the guest launcher first.");
        if (stage.AirSource is not null) PrepareHostAir(stage);
        PrepareGameFolder(stage.StockExecutable);
        // Loopback-only font server for the AIR pages; lives until the companion ends and never blocks the launch.
        await using var fonts = await FrizFontServer.StartAsync(stage.AirRoot, Console.WriteLine);
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        PlatformLease? platform = null;
        AirRemoteProxyHandle? remoteProxy = null;
        MaestroServer? maestro = null;
        var preserveMaestro = false;
        try
        {
            if (stage.RemoteInvitation is { } invitation)
            {
                // A cached bearer that the platform still accepts is used as it is. Otherwise the proxy starts without one: the friend's
                // login in the AIR client supplies it through the login queue.
                var connections = Path.Combine(stage.Root, "connections");
                var cached = await RemoteSignIn.TryCachedTokenAsync(invitation, connections, stop.Token);
                invitation = cached ?? invitation with { Token64hex = "", Bearer = null };
                remoteProxy = await AirRemoteProxy.StartWithPortsAsync(invitation, 8390, 2099, 5222, stop.Token, connections);
                var proxyHealth = new Uri($"http://127.0.0.1:{remoteProxy.HttpPort}/health");
                if (!await WaitForHealthAsync(proxyHealth, stop.Token))
                    throw new IOException($"The remote AIR platform did not become ready within {PlatformStartupTimeout.TotalSeconds:0}s.");
                Console.WriteLine(cached is null
                    ? $"Remote AIR platform ready on local proxy HTTP {remoteProxy.HttpPort}; sign in with the login the host sent you in the AIR client."
                    : $"Connected remote AIR platform; local proxy HTTP {remoteProxy.HttpPort}, RTMP 2099, chat 5222.");
            }
            else platform = await EnsurePlatformAsync(stage, stop.Token);
            if (await ClassicAsync(stage, remoteProxy?.HttpPort ?? stage.HttpPort, clientShown, askClassic, notice, stop.Token) is { } classicExit)
                return classicExit;
            ResetLanguageToEnglish(stage);
            maestro = CreateMaestro(stage.MaestroPort, stage.StockExecutable, stage.RemoteInvitation?.Host ?? stage.GameHost);
            await maestro.StartAsync(stop.Token);
            var exitCode = await RunAirAsync(maestro, CreateAirStartInfo(stage.AirRoot), stop.Token, clientShown);

            if (maestro.HasOwnedGame)
            {
                preserveMaestro = true;
                Console.WriteLine("AIR exited while a native match is active; keeping Maestro available until that match ends.");
                try { await WaitForOwnedGameExitAsync(maestro, stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                preserveMaestro = stop.IsCancellationRequested && maestro.HasOwnedGame;
            }

            await maestro.StopAsync(stopOwnedGame: !preserveMaestro);
            if (preserveMaestro) Console.WriteLine("Cancellation left the active native match and local platform running.");
            return stop.IsCancellationRequested ? 0 : exitCode;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            try
            {
                if (maestro is not null && maestro.IsRunning)
                    await maestro.StopAsync(stopOwnedGame: !preserveMaestro);
            }
            finally
            {
                // A desktop launch owns only the process it started. Keep it alive
                // after AIR exits so a native match or reconnect is never torn down.
                try { platform?.Detach(); }
                finally
                {
                    if (remoteProxy is not null) await remoteProxy.DisposeAsync();
                }
            }
        }
    }

    /// <summary>
    /// NOT RETAIL: the classic start (ClassicStart; design 2026-11-12 §2, §5). Retail's 2011 launcher ran on Riot's update service and started
    /// AIR and the game itself; here the player's own launcher runs on our stand-in for that service, with no Maestro of ours. The exit code once
    /// the classic session has ended; null when the usual start runs instead: no launcher here, the player chose it this time, or a fallback,
    /// whose reason is logged and told once through <paramref name="notice"/>.
    /// </summary>
    private static async Task<int?> ClassicAsync(DesktopStage stage, int newsPort, Action? clientShown, Func<bool>? askClassic, Action<string>? notice,
        CancellationToken stop)
    {
        var root = GameManagedLayout.ResolveRoot();
        var plan = ClassicStart.Decide(stage, root, port => PlayerMode.BusyPort([port]) is null, ClassicStart.MutexFree);
        var reason = plan.Reason;
        var takes = ClassicStart.Takes(plan, stage.Root, askClassic ?? (() => true));
        if (plan.Use && !takes) Console.WriteLine("classic: the usual way this time (the player's choice)");
        if (takes)
        {
            // The AIR lease and repairs come first, as AIR now starts later, from the launcher; the lease is held until the session ends.
            using var lease = PrepareAirCopy(CreateAirStartInfo(stage.AirRoot));
            if (lease is null) return 0;
            // The stand-in's mutex, then the layout, inside RunAsync: one guard covers both.
            reason = await ClassicStart.GuardedAsync(() => ClassicStart.RunAsync(stage, new(root!, stage.LauncherRoot!, stage.AirRoot,
                Path.GetDirectoryName(stage.StockExecutable)!, newsPort), Console.WriteLine, clientShown, stop), stop);
            if (reason is null) return 0;
        }
        if (reason is null) return null;
        var message = ClassicStart.FallbackMessage(reason);
        Console.WriteLine("classic: " + message);
        notice?.Invoke(message);
        return null;
    }

    /// <summary>Everything about the picked game folder that is checked before AIR opens. Never blocks the launch.</summary>
    private static void PrepareGameFolder(string stockExecutable)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(stockExecutable))!;
        Console.WriteLine(GameFolderReport.Describe(directory));
        try { Console.WriteLine(GameLanguagePack.Ensure(directory, GameLanguagePack.ResolveLocale(directory))); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Console.WriteLine($"language-pack: not deployed ({exception.GetType().Name}); the game starts without it"); }
        // After the language pack, which adds files under DATA: the stamp covers them. Background work, never awaited here.
        _ = GameManagedLayout.Start(directory, Console.WriteLine);
    }

    /// <summary>
    /// NOT RETAIL: the host's own play (Amberline Host H12). The stage's AIR copy is made from the host's AIR folder, which is only
    /// read, as the player's is (PlayerMode); again when it is missing or incomplete or the host chose another folder
    /// (<c>air-source.txt</c> names the folder it was made from). Every start points it at the stage's port. True when it was copied.
    /// </summary>
    internal static bool PrepareHostAir(DesktopStage stage)
    {
        var source = stage.AirSource ?? throw new ArgumentException("The stage names no AIR folder.");
        var marker = Path.Combine(stage.Root, "air-source.txt");
        var platform = $"http://127.0.0.1:{stage.HttpPort}";
        if (!PlayerMode.AirCopyNeeded(stage.AirRoot, source, File.Exists(marker) ? File.ReadAllText(marker) : null))
        {
            AirOverlay.Configure(stage.AirRoot, AirOverlay.ParsePlatformUrl(platform), stage.MaestroPort);
            return false;
        }
        PlayerMode.PrepareAirCopy(source, stage.AirRoot, platform, stage.MaestroPort);
        File.WriteAllText(marker, source);
        return true;
    }

    /// <summary>
    /// NOT RETAIL: the usual start of a player (the shortcut, --play, the fallback) or of the host's own Play runs in English (design 2026-11-12 §6).
    /// Retail's game used whatever language its launcher wrote into AIR's locale.properties and the game's locale.cfg; the usual start offers no
    /// language choice, so both go back to en_US before AIR opens. START-AIR (the operator's stage: no invitation and no AIR source) keeps its files,
    /// as the spec's Non-goals say. The classic start never calls this: its launcher sets the player's language itself. A read-only or locked file
    /// is logged and the start goes on.
    /// </summary>
    internal static void ResetLanguageToEnglish(DesktopStage stage)
    {
        if (stage.RemoteInvitation is null && stage.AirSource is null) return;
        try { AirOverlay.SetLocale(stage.AirRoot); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Console.WriteLine($"language: AIR's locale.properties is not set to English ({exception.GetType().Name}); AIR starts as it is"); }
        try { GameLanguagePack.ResetLocaleCfg(Path.GetDirectoryName(Path.GetFullPath(stage.StockExecutable))!); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Console.WriteLine($"language: the game's locale.cfg is not set to English ({exception.GetType().Name}); the game starts as it is"); }
    }

    private static async Task<int> RunManageAsync(string stageRoot)
    {
        var stage = LoadDesktopStage(stageRoot);
        using var stop = new CancellationTokenSource(PlatformStartupTimeout);
        var platform = await EnsurePlatformAsync(stage, stop.Token);
        try
        {
            var manage = new Uri($"http://127.0.0.1:{stage.HttpPort}/manage");
            if (Process.Start(new ProcessStartInfo(manage.AbsoluteUri) { UseShellExecute = true }) is null)
                throw new IOException("The local management browser could not be opened.");
            Console.WriteLine($"Opened local AIR management at {manage}.");
            return 0;
        }
        finally { platform.Detach(); }
    }

    public static DesktopStage LoadDesktopStage(string stageRoot, int requestedMaestroPort = 8393,
        string? remoteInvitationPath = null)
    {
        var root = Path.GetFullPath(stageRoot ?? throw new ArgumentNullException(nameof(stageRoot)));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"The AIR stage directory was not found: {root}");
        var stage = ReadJson(Path.Combine(root, "stage.json"));
        var stockValue = ReadOptionalString(stage, "stockExecutable");
        var stock = stockValue is null ? string.Empty : Path.GetFullPath(stockValue);
        var platformData = Path.Combine(root, "platform-data");
        // An installed player's server stage names the install's AIR folder, which all its servers share (PlayerInstall).
        var installedAir = ReadOptionalString(stage, "airRoot");
        var airRoot = installedAir is { } air ? Path.GetFullPath(air) : Path.Combine(root, "air");
        // NOT RETAIL: the classic start's launcher folder. The stage may name it; an installed player's otherwise sits beside the install's
        // AIR folder (<install>\launcher); the host app's stage names it or has none; so does the operator's (START-AIR; owner, 13 November
        // 2026: "update my desktop START-AIR to open the launcher first").
        var launcherRoot = ReadOptionalString(stage, "launcherRoot") is { } launcher ? Path.GetFullPath(launcher) : null;
        var maestroPort = ReadOptionalInt(stage, "maestroPort") ?? requestedMaestroPort;
        if (maestroPort is < 1 or > 65535) throw new ArgumentException("maestroPort must be between 1 and 65535.");
        var connections = Path.Combine(root, "connections");
        var configuredInvitation = remoteInvitationPath is null
            ? new[] { Path.Combine(connections, "server.json"), Path.Combine(connections, "air-invitation.json") }
                .FirstOrDefault(path => File.Exists(path))
            : Path.GetFullPath(remoteInvitationPath);
        if (configuredInvitation is not null && File.Exists(configuredInvitation))
        {
            var invitation = ReadRemoteInvitation(configuredInvitation);
            return new DesktopStage(root, airRoot, stock,
                Path.Combine(root, "platform", "Amberline.Platform.exe"), platformData, 8390, maestroPort, invitation,
                LauncherRoot: installedAir is null ? null
                    : launcherRoot ?? Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(airRoot)) ?? airRoot, "launcher"));
        }
        if (remoteInvitationPath is not null)
            throw new FileNotFoundException("The selected remote AIR connection was not found.", configuredInvitation);
        if (TryGetProperty(stage, "remote", out var remote) && remote.ValueKind == JsonValueKind.True)
            throw new FileNotFoundException("Place the host connection in connections/server.json beside this client package (legacy air-invitation.json is also accepted).", Path.Combine(connections, "server.json"));
        if (stockValue is null) throw new ArgumentException("Stage configuration is missing stockExecutable.");
        // The host's own stage (Play on this PC, written by the home Platform) names the running Platform's port, the host's AIR
        // folder to copy and the local game address; the operator's stage keeps the port in platform-data\platform.json.
        if (ReadOptionalInt(stage, "httpPort") is { } stagedPort)
        {
            if (stagedPort is < 1 or > 65535) throw new ArgumentException("httpPort must be between 1 and 65535.");
            return new DesktopStage(root, airRoot, stock, Path.Combine(root, "platform", "Amberline.Platform.exe"), platformData, stagedPort,
                maestroPort, AirSource: ReadOptionalString(stage, "airSource") is { } source ? Path.GetFullPath(source) : null,
                GameHost: ReadOptionalString(stage, "gameHost"), LauncherRoot: launcherRoot);
        }
        var platformSettings = ReadJson(Path.Combine(platformData, "platform.json"));
        var httpPort = ReadRequiredInt(platformSettings, "HttpPort");
        if (httpPort is < 1 or > 65535) throw new ArgumentException("platform.json HttpPort must be between 1 and 65535.");
        return new DesktopStage(root, airRoot, stock,
            Path.Combine(root, "platform", "Amberline.Platform.exe"), platformData, httpPort, maestroPort, LauncherRoot: launcherRoot);
    }

    public static bool IsReadyHealth(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("status", out var status) &&
                   status.ValueKind == JsonValueKind.String &&
                   string.Equals(status.GetString(), "ready", StringComparison.OrdinalIgnoreCase) &&
                   root.TryGetProperty("service", out var service) &&
                   service.ValueKind == JsonValueKind.String &&
                   string.Equals(service.GetString(), "Amberline.AirPlatform", StringComparison.Ordinal) &&
                   root.TryGetProperty("protocol", out var protocol) &&
                   protocol.ValueKind == JsonValueKind.Number &&
                   protocol.TryGetInt32(out var version) && version == 1 &&
                   root.TryGetProperty("client", out var client) &&
                   client.ValueKind == JsonValueKind.String &&
                    string.Equals(client.GetString(), ExpectedAirClientIdentity, StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }

    public static ProcessStartInfo CreatePlatformStartInfo(string stageRoot, string platformExecutable,
        string platformData, string logPath)
    {
        var executable = Path.GetFullPath(platformExecutable);
        var diagnostics = Path.GetFullPath(logPath);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            WorkingDirectory = Path.GetFullPath(stageRoot),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Let cmd own the redirection so the staged platform can outlive this
        // companion without losing its durable diagnostics pipe.
        // Keep the complete /c command in raw Arguments. ProcessStartInfo's
        // ArgumentList applies CRT escaping, which changes cmd.exe's special
        // outer-quote rule when either path contains spaces.
        start.Arguments = $"/d /s /c \"\"{executable}\" 1>>\"{diagnostics}\" 2>&1\"";
        start.Environment["AMBERLINE_PLATFORM_HOME"] = Path.GetFullPath(platformData);
        start.Environment["AMBERLINE_PACKET_TRACE"] = "1";
        start.Environment["AMBERLINE_PACKET_TRACE_BUDGET_MB"] = "1024";
        start.Environment["LOG_FIDELITY_TRACE"] = "3";
        // The retained 20-minute playtest exhausted 256 MiB before 18:35.
        // Keep the existing cadence and bounded recorder, with room for the
        // long-match acceptance window.
        start.Environment["LOG_FIDELITY_BUDGET_MB"] = "1024";
        start.Environment["LOG_COLLISION"] = "1";
        start.Environment["AMBERLINE_AIR_PLATFORM_LOG"] = diagnostics;
        return start;
    }

    private static MaestroServer CreateMaestro(int port, string stockExecutable, string? remoteHost = null)
    {
        var maestro = remoteHost is null
            ? new MaestroServer(port, stockExecutable)
            : new MaestroServer(port, request => new StockClientLauncher(stockExecutable, port).Launch(
                request with { ServerHost = remoteHost }));
        maestro.PeerConnected += () => Console.WriteLine("maestro.peer-connected");
        maestro.NativeGameConnected += () => Console.WriteLine("maestro.game-connected (native message received)");
        maestro.MessageReceived += message => Console.WriteLine($"maestro.received type={message.Type} bytes={message.Payload.Length}");
        return maestro;
    }

    /// <summary>
    /// What comes before AIR starts, ours or the classic launcher's: the lease, the running-copy check and the copy's repairs. Null when an
    /// orphaned copy's hidden window was restored instead (start nothing, stop here); else the lease, which the caller holds until its start ends.
    /// </summary>
    internal static IDisposable? PrepareAirCopy(ProcessStartInfo airStart)
    {
        var airRoot = Path.GetFullPath(airStart.WorkingDirectory ?? Environment.CurrentDirectory);
        // Serialize launches for one staged copy and hold the lease until the
        // AIR process exits.  The path check catches a copy started by an
        // older companion that did not create this lease file.
        var airCopyLease = LeaverDataRepair.AcquireAirCopyLease(airRoot);
        try
        {
            var runningCopies = LeaverDataRepair.RunningSameAirCopyProcessIds(airStart);
            // NOT RETAIL (the classic start): its launcher starts AIR through the layout's junction, a second path to a copy on this PC.
            if (runningCopies.Count == 0 && GameManagedLayout.ResolveRoot() is { } radsRoot)
                runningCopies = LeaverDataRepair.RunningSameAirCopyProcessIds(new ProcessStartInfo(ClassicLauncherLayout.Images(radsRoot).Air));
            if (runningCopies.Count > 0)
            {
                // An orphaned copy (its companion died) can sit hidden in the tray. Restore its window instead of
                // refusing; no second copy is started, so this companion stops here.
                if (AirWindowDiagnostics.RestoreHiddenWindows(runningCopies) > 0)
                {
                    Console.WriteLine("The AIR copy is already running; restored its hidden window and started no second copy.");
                    airCopyLease.Dispose();
                    return null;
                }
                throw new InvalidOperationException("The AIR copy is already running; close it before starting another companion.");
            }

            var storyMigrationCount = AirOverlay.MigrateStoryPageUrl(airRoot);
            if (storyMigrationCount != 0)
                Console.WriteLine($"AIR story route migration: updated {storyMigrationCount} generated properties file(s).");

            var preferenceRepair = LeaverDataRepair.RepairGlobalPreferences(airRoot);
            var backupNote = preferenceRepair.BackupPath is null ? string.Empty : $"; backup={preferenceRepair.BackupPath}";
            Console.WriteLine($"AIR preference repair: {preferenceRepair.Status} ({preferenceRepair.Reason}){backupNote}");
            return airCopyLease;
        }
        catch
        {
            airCopyLease.Dispose();
            throw;
        }
    }

    private static async Task<int> RunAirAsync(MaestroServer maestro, ProcessStartInfo airStart, CancellationToken stop, Action? clientShown = null)
    {
        using var airCopyLease = PrepareAirCopy(airStart);
        if (airCopyLease is null) return 0;

        using var air = new Process { StartInfo = airStart };
        air.OutputDataReceived += (_, output) => { if (output.Data is not null) Console.WriteLine("air: " + output.Data); };
        // ADL writes ordinary trace output to stderr too. Keep every line,
        // but don't turn those traces into PowerShell NativeCommandError records.
        air.ErrorDataReceived += (_, output) => { if (output.Data is not null) Console.WriteLine("air.stderr: " + output.Data); };
        if (!air.Start()) throw new IOException("The disposable AIR client could not be started.");
        air.BeginOutputReadLine();
        air.BeginErrorReadLine();
        Console.WriteLine($"Maestro listening on 127.0.0.1:{maestro.ListeningPort}; AIR PID {air.Id}.");
        using var windowStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var windowWatch = AirWindowDiagnostics.WatchAsync(air, maestro, windowStop.Token);
        if (clientShown is not null) _ = WhenWindowShownAsync(air, clientShown, windowStop.Token);
        try { await air.WaitForExitAsync(stop); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            windowStop.Cancel();
            try { await windowWatch; }
            catch { /* A read-only window probe must never affect AIR lifecycle. */ }
        }
        if (!air.HasExited) air.Kill(entireProcessTree: true);
        await air.WaitForExitAsync();
        var exitCode = air.ExitCode;
        Console.WriteLine($"AIR exited with code {exitCode}.");
        return stop.IsCancellationRequested ? 0 : exitCode;
    }

    /// <summary>
    /// NOT RETAIL: calls <paramref name="shown"/> once AIR's main window is visible, or AIR has ended, or two minutes passed; it never
    /// throws, as it only closes the shortcut's start window.
    /// </summary>
    private static async Task WhenWindowShownAsync(Process air, Action shown, CancellationToken stop)
    {
        try
        {
            for (var poll = 0; poll < 480 && !air.HasExited; poll++)
            {
                air.Refresh();
                if (air.MainWindowHandle != 0) break;
                await Task.Delay(250, stop);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { shown(); }
    }

    private static async Task<PlatformLease> EnsurePlatformAsync(DesktopStage stage, CancellationToken stop)
    {
        var health = new Uri($"http://127.0.0.1:{stage.HttpPort}/health");
        if (await IsPlatformReadyAsync(health, stop))
        {
            Console.WriteLine($"Reusing healthy platform at {health}.");
            return PlatformLease.Existing();
        }

        if (!File.Exists(stage.PlatformExecutable))
            throw new FileNotFoundException("The staged platform executable was not found.", stage.PlatformExecutable);
        await using var startupLease = await AcquirePlatformStartupLeaseAsync(stage.Root, stop);
        {
            if (await IsPlatformReadyAsync(health, stop))
            {
                Console.WriteLine($"Reusing platform started by another desktop launch at {health}.");
                return PlatformLease.Existing();
            }

            var logPath = CreatePlatformLogPath(stage.Root);
            var start = CreatePlatformStartInfo(stage.Root, stage.PlatformExecutable, stage.PlatformData, logPath);
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            var lease = new PlatformLease(process, logPath);
            try
            {
                if (!process.Start()) throw new IOException("The local platform could not be started.");
                Console.WriteLine($"Started local platform PID {process.Id}; diagnostics: {logPath}");
                if (!await WaitForPlatformReadyAsync(health, process, stop))
                {
                    var reason = process.HasExited ? $"exited with code {process.ExitCode} before readiness" :
                        $"did not become ready within {PlatformStartupTimeout.TotalSeconds:0}s";
                    throw new IOException($"The local platform {reason}; diagnostics: {logPath}");
                }
                return lease;
            }
            catch
            {
                await lease.StopAfterStartupFailureAsync();
                throw;
            }
        }
    }

    private static async Task<FileStream> AcquirePlatformStartupLeaseAsync(string stageRoot, CancellationToken stop)
    {
        var directory = Path.Combine(stageRoot, "platform-data");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ".air-platform-start.lock");
        var deadline = DateTime.UtcNow + PlatformStartupTimeout;
        while (true)
        {
            stop.ThrowIfCancellationRequested();
            FileStream? lease = null;
            try
            {
                lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return lease;
            }
            catch (IOException)
            {
                lease?.Dispose();
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("Timed out waiting for another desktop launch to start the local platform.");
                await Task.Delay(PlatformProbeInterval, stop);
            }
        }
    }

    private static async Task<bool> IsPlatformReadyAsync(Uri health, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(PlatformHealthTimeout);
        try
        {
            using var client = new HttpClient { Timeout = PlatformHealthTimeout };
            using var response = await client.GetAsync(health, timeout.Token);
            return response.IsSuccessStatusCode && IsReadyHealth(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        { return false; }
    }

    private static async Task<bool> WaitForHealthAsync(Uri health, CancellationToken stop)
    {
        var deadline = DateTime.UtcNow + PlatformStartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            stop.ThrowIfCancellationRequested();
            if (await IsPlatformReadyAsync(health, stop)) return true;
            await Task.Delay(PlatformProbeInterval, stop);
        }
        return false;
    }

    private static async Task<bool> WaitForPlatformReadyAsync(Uri health, Process process, CancellationToken stop)
    {
        var deadline = DateTime.UtcNow + PlatformStartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            stop.ThrowIfCancellationRequested();
            if (process.HasExited) return false;
            if (await IsPlatformReadyAsync(health, stop)) return true;
            await Task.Delay(PlatformProbeInterval, stop);
        }
        return false;
    }

    private static async Task WaitForOwnedGameExitAsync(MaestroServer maestro, CancellationToken stop)
    {
        while (maestro.HasOwnedGame) await Task.Delay(TimeSpan.FromSeconds(1), stop);
    }

    private static string CreatePlatformLogPath(string stageRoot)
    {
        var logs = Path.Combine(stageRoot, "logs");
        Directory.CreateDirectory(logs);
        var stem = Path.Combine(logs, $"platform-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
        for (var index = 0; index < 100; index++)
        {
            var path = stem + (index == 0 ? ".log" : $"-{index}.log");
            try
            {
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
                return path;
            }
            catch (IOException) when (index < 99) { }
        }
        throw new IOException("Unable to create a unique local platform diagnostics log.");
    }

    private static JsonElement ReadJson(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The desktop stage configuration is missing.", path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    internal static AirRemoteInvitation ReadRemoteInvitation(string path)
    {
        var invitation = JsonSerializer.Deserialize<AirRemoteInvitation>(File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (invitation is null || invitation.Host is null || invitation.Token64hex is null || invitation.CertificateSha25664hex is null)
            throw new ArgumentException("The remote AIR invitation is missing required fields.");
        invitation.Validate();
        return invitation;
    }

    private static string? ReadOptionalString(JsonElement root, string name) => TryGetProperty(root, name, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
        ? value.GetString() : null;

    private static int ReadRequiredInt(JsonElement root, string name) => TryGetProperty(root, name, out var value) &&
        value.TryGetInt32(out var number) ? number : throw new ArgumentException($"Stage configuration is missing {name}.");

    private static int? ReadOptionalInt(JsonElement root, string name) => TryGetProperty(root, name, out var value) &&
        value.TryGetInt32(out var number) ? number : null;

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value)) return true;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private sealed class PlatformLease(Process? process, string? logPath)
    {
        public static PlatformLease Existing() => new(null, null);

        public async Task StopAfterStartupFailureAsync()
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }

        public void Detach()
        {
            if (process is null) return;
            try
            {
                if (!process.HasExited)
                    Console.WriteLine($"Local platform remains running (PID {process.Id}); diagnostics: {logPath}");
            }
            catch (InvalidOperationException) { }
            finally { process.Dispose(); }
        }
    }

    public static ProcessStartInfo CreateAirStartInfo(string directory)
    {
        var root = Path.GetFullPath(directory);
        var executable = Path.Combine(root, "LolClient.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("The AIR copy does not contain the AIR client's executable.", executable);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        // This preserved bundle names Adobe's adl.exe LolClient.exe. Packaged
        // AIR bootstrappers launch directly; ADL requires the descriptor/root.
        if (string.Equals(FileVersionInfo.GetVersionInfo(executable).OriginalFilename, "adl.exe", StringComparison.OrdinalIgnoreCase))
        {
            var descriptor = Path.Combine(root, "META-INF", "AIR", "application.xml");
            var runtime = Path.Combine(root, "Adobe AIR", "Versions", "1.0", "Adobe AIR.dll");
            foreach (var required in new[] { descriptor, runtime, Path.Combine(root, "LolClient.swf") })
                if (!File.Exists(required)) throw new FileNotFoundException("Required AIR startup file is missing.", required);
            // ADL's Windows -runtime path is the parent of the Adobe AIR folder. -nodebug (batch 686) is ADL's installed-application
            // mode: no debugger connection and no window for an unhandled exception; trace output still reaches stderr, so the
            // client's own "Uncaught Error" log lines (ClientModuleMain.onUncaughtError) stay in the desktop log. Without it every
            // stock-client error a player meets opens an "ActionScript error" window, e.g. the fifteen #1009s the second open of the
            // profile raises inside Flex's TileList (SummonerProfileController.updateSpellsFromInventory, batch 686 record).
            // Reconstruction: the retail launcher's arguments for this adl.exe are not in the shipped files; an installed client
            // showed no such windows.
            start.ArgumentList.Add("-runtime");
            start.ArgumentList.Add(root);
            // DIAGNOSTICS: AMBERLINE_AIR_DEBUG_WINDOWS=1 at launch leaves out -nodebug, so a tester sees the AIR runtime's error
            // windows again (batch 686b, owner, 4 November 2026). Players never set it.
            if (Environment.GetEnvironmentVariable("AMBERLINE_AIR_DEBUG_WINDOWS") != "1") start.ArgumentList.Add("-nodebug");
            start.ArgumentList.Add(descriptor);
            start.ArgumentList.Add(root);
        }
        return start;
    }

    private static int ParsePort(string? value) =>
        string.IsNullOrWhiteSpace(value) ? 8393 : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? port : throw new ArgumentException("--maestro-port must be between 1 and 65535.");

    private static string Required(string[] args, string option) => Option(args, option) is { Length: > 0 } value
        ? value : throw new ArgumentException($"{option} requires a value.");

    internal static string? Option(string[] args, string option)
    {
        var index = Array.FindIndex(args, value => string.Equals(value, option, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("-", StringComparison.Ordinal) ? args[index + 1] : null;
    }

    private static void PrintUsage() => Console.WriteLine(
        "Amberline.AirCompanion --play [--game-folder <folder>] [--air-folder <folder>] [--connection <server.json>]\n" +
        "Amberline.AirCompanion --install --connection <server.json> [--server-name <name>]\n" +
        "Amberline.exe --shortcut <server id>   (the desktop shortcut's windowless copy, made by --install)\n" +
        "Amberline.exe --uninstall              (removes a server, or Amberline itself; Uninstall.cmd in the install folder)\n" +
#if AMBERLINE_ARCHIVE_INSTALLER
        "Amberline.AirCompanion --install-files --root <folder>\n" +
#endif
        "Amberline.AirCompanion --prepare-only --source <lol_air_client> --destination <copy> [--platform-url URL] [--maestro-port N]\nAmberline.AirCompanion --start --air-root <copy> --stock-executable <League of Legends.exe> [--maestro-port N]\nAmberline.AirCompanion --desktop --stage-root <preview> [--remote-invitation <path>] [--maestro-port N]\nAmberline.AirCompanion --manage --stage-root <preview>\nAmberline.AirCompanion --register [--platform-url URL]");
}
