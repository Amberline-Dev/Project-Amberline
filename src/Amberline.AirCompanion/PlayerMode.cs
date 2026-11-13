using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Amberline.AirCompanion;

/// <summary>
/// NOT RETAIL: bring-your-own-clients mode (<c>--play</c>, started by <c>Play.cmd</c>). The player keeps the stock 1.0.0.126 game and AIR
/// folders they downloaded and receives one connection file from the operator (<c>server.json</c>: host, HTTPS port, certificate pin).
/// Nothing is downloaded and no player file is changed: AIR is copied into the player's stage as the friend launcher does (the source is
/// only read), the game starts from its own folder, and the friend's login arrives through AIR. The choices live in the stage
/// (<c>stage.json</c>, <c>connections\server.json</c>), so a later run starts without asking.
/// </summary>
internal static class PlayerMode
{
    private const string GameFile = "League of Legends.exe";
    private const string AirFile = "LolClient.exe";
    private const int MaestroPort = 8393;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The player's stage, per Windows user: <c>%LOCALAPPDATA%\Amberline Player\stage</c> (<see cref="FolderMove.PlayerFolder"/>).</summary>
    internal static string StageRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) throw new IOException("Windows LocalAppData is unavailable.");
        return Path.Combine(FolderMove.PlayerFolder(local), "stage");
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var stage = StageRoot();
        var rememberedAir = Remembered(stage, "airSource");
        var rememberedGame = Remembered(stage, "stockExecutable") is { } executable ? Path.GetDirectoryName(executable) : null;
        string gameFolder, airFolder;
        AirRemoteInvitation connection;
        try
        {
            gameFolder = Folder("game folder", GameFile, Program.Option(args, "--game-folder") ?? rememberedGame, folder => RetailClients.GameProblem(folder));
            airFolder = Folder("AIR folder", AirFile, Program.Option(args, "--air-folder") ?? rememberedAir, folder => RetailClients.AirProblem(folder));
            connection = ConnectionFor(args, stage);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Setup canceled. Run Play.cmd again when you are ready.");
            return 1;
        }
        var copy = Path.Combine(stage, "air");
        if (AirCopyNeeded(copy, airFolder, rememberedAir)) PrepareAirCopy(airFolder, copy);
        WriteStage(stage, gameFolder, airFolder, connection);
        return await Program.RunDesktopAsync(stage, MaestroPort, null);
    }

    /// <summary>
    /// NOT RETAIL: the desktop shortcut of an installed player (<c>Amberline.exe --shortcut &lt;server id&gt;</c>, PlayerInstall), with no
    /// console window: the output goes to the server's <c>logs\shortcut.log</c> and a failure is shown in a message box. It first asks the
    /// server for a newer player tool (PlayerInstall.StageUpdateAsync); a staged one is started to finish the switch once this program has
    /// exited (<c>--finish-update &lt;process id&gt;</c>), as a running program cannot replace its own files. Then it plays from the install
    /// folder directly: the server's stage names the install's AIR and game folders, so no second copy is made. Batch 639: the start is
    /// never silent. A small window says "Checking for updates…", then "Starting the game client…", and closes when the client's
    /// window appears; a failure is a dialog with what happened, what to do, and Try again.
    /// </summary>
    public static async Task<int> RunShortcutAsync(string[] args)
    {
        var tool = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var install = Path.GetDirectoryName(tool)!;
        var id = Program.Option(args, "--shortcut") is { } value && System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9a-f]{16}$") ? value : null;
        var server = id is null ? null : Path.Combine(install, "servers", id);
        if (server is null || !File.Exists(Path.Combine(server, "server.json")))
        {
            PlayerInstallWindow.ShowProblem(new(PlayerInstall.DefaultServerName, "This shortcut's server is not set up any more.",
                "Double-click your server's Play file again: it sets the server up and makes a new shortcut.",
                $"No connection at {(server is null ? "(the shortcut names no server)" : Path.Combine(server, "server.json"))}", null));
            return 2;
        }
        var title = Remembered(server, "name", "server.json") ?? PlayerInstall.DefaultServerName;
        var log = Path.Combine(server, "logs", "shortcut.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            if (File.Exists(log)) File.Move(log, Path.Combine(server, "logs", "shortcut.previous.log"), overwrite: true);
            var writer = TextWriter.Synchronized(new StreamWriter(log, append: false, new UTF8Encoding(false)) { AutoFlush = true });
            Console.SetOut(writer);
            Console.SetError(writer);
        }
        // Another start of this shortcut holds the log: this one runs without a log and meets the running client below.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Console.SetOut(TextWriter.Null); }
        var finishing = Program.Option(args, "--finish-update") is { } previous && int.TryParse(previous, out var processId) ? processId : (int?)null;
        if (finishing is { } old) await FinishUpdateAsync(old, tool, install);
        using var status = new PlayerInstallWindow.StatusWindow(title);
        return await StartAsync(install, id!, checkUpdate: finishing is null, ShortcutUi.Windows(status, title));
    }

    /// <summary>
    /// DIAGNOSTICS: the shortcut's windows and its play step as delegates (a test seam), so a check drives a start, its failures and Try
    /// again without a window and without starting a client. <see cref="Play"/> gets the server folder, its connection file and the
    /// callback that closes the start window once the League client's window shows. Batch 697: <see cref="Leftover"/> asks about a
    /// client left from an earlier start (true for Close and start; the flag says whether closing is offered), and <see cref="Ports"/>
    /// are the local ports the play step binds, so a check uses free ports instead of the real ones. NOT RETAIL (the classic start, design
    /// 2026-11-12 §2): the windows' play step hands Program.RunDesktopAsync the next-start question (true for "Open the classic launcher
    /// again") and the start window as the fallback's notice.
    /// </summary>
    internal sealed record ShortcutUi(Action<string> Status, Action Hide, Func<PlayerProblem, bool> TryAgain, Func<PlayerProblem, bool, bool> Leftover,
        Func<string, string, Action, Task<int>> Play, IReadOnlyList<int> Ports)
    {
        public static ShortcutUi Windows(PlayerInstallWindow.StatusWindow status, string title)
        {
            // "The usual way this time" shows the start window the classic start kept hidden.
            Func<bool> askClassic = () =>
            {
                if (PlayerInstallWindow.AskClassic(title)) return true;
                status.Show(Starting);
                return false;
            };
            return new(status.Show, status.Hide,
                problem =>
                {
                    status.Hide();
                    return PlayerInstallWindow.AskTryAgain(problem);
                },
                (problem, canClose) =>
                {
                    status.Hide();
                    return PlayerInstallWindow.AskCloseLeftover(problem, canClose);
                },
                (server, connection, shown) => Program.RunDesktopAsync(server, MaestroPort, connection, shown, askClassic, status.Show), LocalPorts);
        }
    }

    /// <summary>
    /// The loopback ports the play step binds (Program.RunDesktopAsync): the proxy's RTMP 2099 (bound first), chat 5222 and HTTP 8390,
    /// then Maestro. The font server's port is left out: a busy one never blocks the start.
    /// </summary>
    internal static readonly IReadOnlyList<int> LocalPorts = [2099, 5222, 8390, MaestroPort];

    // Batch 653: each wait says how long it takes.
    internal const string Checking = "Checking for updates. This takes a few seconds.";
    internal const string Starting = "Starting the game client. This takes up to a minute.";
    internal const string Closing = "Closing the game client from the earlier start. This takes a few seconds.";

    /// <summary>
    /// One start of an installed server: its clients must be there, the update check runs (an unreachable server or one that fails the
    /// pin stops here: the League client could not sign in to it either), then play. Every failure asks Try again; Close ends with 2.
    /// </summary>
    internal static async Task<int> StartAsync(string install, string id, bool checkUpdate, ShortcutUi ui)
    {
        var server = Path.Combine(install, "servers", id);
        var connection = Path.Combine(server, "server.json");
        var title = Remembered(server, "name", "server.json") ?? PlayerInstall.DefaultServerName;
        var log = Path.Combine(server, "logs", "shortcut.log");
        // NOT RETAIL (design 2026-11-12 §5): with the accepted 2011 launcher, the launcher is the first window. Ours stays hidden; a problem
        // still shows its dialog, and a fallback its notice. A stage that cannot be read gets the usual window; the play step reports it.
        string? launcher = null;
        try { launcher = Program.LoadDesktopStage(server, MaestroPort, connection).LauncherRoot; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        var quiet = launcher is not null && ClassicLauncherFiles.Problem(launcher) is null;
        try
        {
            while (true)
            {
                if (!quiet) ui.Status(Checking);
                PlayerProblem problem;
                try
                {
                    if (MissingClient(server, title, log) is { } missing) problem = missing;
                    else if (!await ClearLeftoversAsync(install, server, title, log, ui)) return 2;
                    else if (BusyPort(ui.Ports) is { } port) problem = PortInUse(title, port, $"127.0.0.1:{port} could not be bound on this PC.", log);
                    else if (checkUpdate && await StagedUpdateAsync(install, server) is { } staged)
                    {
                        StartStaged(staged, id);
                        return 0;
                    }
                    else
                    {
                        if (!quiet) ui.Status(Starting);
                        return await ui.Play(server, connection, ui.Hide);
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Console.WriteLine(exception.ToString());
                    problem = StartProblem(exception, title, Remembered(server, "host", "server.json"), log);
                }
                Console.WriteLine($"start: {problem.WhatHappened}");
                if (!ui.TryAgain(problem)) return 2;
            }
        }
        finally { ui.Hide(); }
    }

    /// <summary>A start that failed, in the player's words. The server's address is never shown: the Play file keeps it hidden too.</summary>
    internal static PlayerProblem StartProblem(Exception exception, string title, string? host, string log)
    {
        var details = PlayerProblem.Describe(exception);
        if (!string.IsNullOrEmpty(host)) details = details.Replace(host, "the server", StringComparison.OrdinalIgnoreCase);
        for (var inner = exception; inner is not null; inner = inner.InnerException)
            if (inner is System.Security.Authentication.AuthenticationException)
                return new(title, $"{title} did not prove it is the server this shortcut was made for.",
                    "Ask the host for a new Play file and double-click it.", details, log);
        // Batch 697: a local listener that could not bind (taken after BusyPort looked) is this PC's port, not the server.
        if (exception is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }) return PortInUse(title, null, details, log);
        if (exception is HttpRequestException { StatusCode: null } or TaskCanceledException or SocketException)
            return new(title, $"Can't reach {title}.",
                "Check that you are online and that the host's server is on, then choose Try again.", details, log);
        return new(title, $"{title} could not start.",
            "Choose Try again. If it keeps happening, choose Copy and send the text to the host.", details, log);
    }

    /// <summary>The install's AIR or game client is gone (moved or deleted since the install): the Play file repairs it.</summary>
    private static PlayerProblem? MissingClient(string server, string title, string log)
    {
        var air = Remembered(server, "airRoot");
        var game = Remembered(server, "stockExecutable");
        string? label = null, missing = null;
        if (air is null || !File.Exists(Path.Combine(air, AirFile))) (label, missing) = ("AIR client", air ?? "(not recorded)");
        else if (game is null || !File.Exists(game)) (label, missing) = ("game client", game is null ? "(not recorded)" : Path.GetDirectoryName(game)!);
        return label is null ? null : new(title, $"The {label} is missing.",
            $"{title} can't find it in {missing}. Double-click {title}'s Play file again to repair the setup.",
            $"Missing: {missing}", log);
    }

    /// <summary>
    /// NOT RETAIL (batch 697; retail had no companion): a client from an earlier start that is still running. An AIR client that did
    /// not exit keeps its companion waiting, and that companion holds the local ports this start binds, which used to read "Can't
    /// reach the server". Only processes started from this install's own folders count (the install, the server's AIR folder and its
    /// game executable, and the classic start's launcher, AIR and game paths: <see cref="Ours"/>), never anything else on the PC. A
    /// running game is never closed: the player gets Cancel only. Otherwise the player chooses Close and start or Cancel. False when the
    /// start stops here.
    /// </summary>
    private static async Task<bool> ClearLeftoversAsync(string install, string server, string title, string log, ShortcutUi ui)
    {
        var leftovers = Leftovers(install, Remembered(server, "airRoot"), Remembered(server, "stockExecutable"));
        try
        {
            if (leftovers.Count == 0) return true;
            var details = "Still running from this install:\r\n" + string.Join("\r\n", leftovers.Select(left => $"{left.Image} (process {left.Process.Id})"));
            var game = leftovers.Any(left => IsGame(left.Image));
            var question = game
                ? new PlayerProblem(title, "A game is in progress on this PC.", "Finish the game first, then double-click the shortcut again.", details, log)
                : new PlayerProblem(title, "A game client from an earlier start is still running in the background.", "Close it and start?", details, log);
            Console.WriteLine($"start: {question.WhatHappened}\r\n{details}");
            if (!ui.Leftover(question, !game)) return false;
            ui.Status(Closing);
            await CloseAsync(leftovers, ui.Ports);
            return true;
        }
        finally
        {
            foreach (var (process, _) in leftovers) process.Dispose();
        }
    }

    /// <summary>The running processes (but this one) that are <see cref="Ours"/>, with the RADS root the classic start uses.</summary>
    internal static List<(Process Process, string Image)> Leftovers(string install, string? air, string? game)
    {
        var radsRoot = GameManagedLayout.ResolveRoot();
        var found = new List<(Process, string)>();
        foreach (var process in Process.GetProcesses())
        {
            string? image = null;
            // A process this user cannot inspect (another user's, an elevated or protected one) is not ours to close.
            try { if (process.Id != Environment.ProcessId) image = process.MainModule?.FileName; }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            if (image is not null && Ours(image, install, air, game, radsRoot)) found.Add((process, image));
            else process.Dispose();
        }
        return found;
    }

    /// <summary>
    /// Whether <paramref name="image"/> is inside <paramref name="install"/> or <paramref name="air"/>, or is <paramref name="game"/>. NOT RETAIL
    /// (the classic start, design 2026-11-12 §5): also the launcher, AIR and the game as a classic start runs them under
    /// <paramref name="radsRoot"/> (its launcher copy and the layout's junctions), so a second start finds that session too.
    /// </summary>
    internal static bool Ours(string image, string install, string? air, string? game, string? radsRoot)
    {
        if (FolderMove.Inside(image, install) || air is not null && FolderMove.Inside(image, air) || game is not null && FolderMove.Same(image, game)) return true;
        if (radsRoot is null) return false;
        var classic = ClassicLauncherLayout.Images(radsRoot);
        return FolderMove.Same(image, classic.Launcher) || FolderMove.Same(image, classic.Air) || FolderMove.Same(image, classic.Game);
    }

    /// <summary>A running game is never closed, whichever path it runs from.</summary>
    internal static bool IsGame(string image) => Path.GetFileName(image).Equals(GameFile, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gracefully first: a process with a window is asked to close it; one with none (the windowless companion, an AIR client whose
    /// window is gone) has nothing to ask and ends at once, as does one still running after five seconds. Then the ports get as long.
    /// </summary>
    private static async Task CloseAsync(List<(Process Process, string Image)> leftovers, IReadOnlyList<int> ports)
    {
        foreach (var (process, image) in leftovers)
            try
            {
                Console.WriteLine($"start: closing {image} (process {process.Id})");
                if (!process.CloseMainWindow()) process.Kill();
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        var deadline = Environment.TickCount64 + 5000;
        foreach (var (process, _) in leftovers)
            try
            {
                if (!process.WaitForExit(TimeSpan.FromMilliseconds(Math.Max(0, deadline - Environment.TickCount64)))) process.Kill();
                process.WaitForExit(5000);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        for (var poll = 0; poll < 20 && BusyPort(ports) is not null; poll++) await Task.Delay(250);
    }

    /// <summary>
    /// The first of <paramref name="ports"/> the start cannot bind on loopback (another program listens there, or Windows keeps it), or
    /// null. Each is bound and released at once, the way the play step binds it.
    /// </summary>
    internal static int? BusyPort(IReadOnlyList<int> ports)
    {
        foreach (var port in ports)
        {
            var probe = new TcpListener(IPAddress.Loopback, port);
            try { probe.Start(); }
            catch (SocketException) { return port; }
            finally { probe.Stop(); }
        }
        return null;
    }

    /// <summary>A local port another program holds: the server is not to blame, so the dialog never says "Can't reach".</summary>
    private static PlayerProblem PortInUse(string title, int? port, string details, string log) =>
        new(title, port is null ? "A port this start needs is in use by another program." : $"Port {port} is in use by another program.",
            $"{title} needs it on this PC. Close the other program, then choose Try again. If you can't tell which program it is, restart the PC.",
            details, log);

    private static void StartStaged(string staged, string id)
    {
        var start = new ProcessStartInfo(Path.Combine(staged, PlayerInstall.WindowlessName)) { UseShellExecute = false, WorkingDirectory = staged };
        foreach (var argument in new[] { "--shortcut", id, "--finish-update", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        using (Process.Start(start)) { }
        Console.WriteLine("update: started the new player tool to finish the switch");
    }

    /// <summary>
    /// The update check: a server that cannot be reached or fails the pin stops the start (the exception goes on); one that answers but
    /// wrongly (an HTTP error, a manifest for another port, a bad archive) leaves the current tool running.
    /// </summary>
    private static async Task<string?> StagedUpdateAsync(string install, string server)
    {
        try { return await PlayerInstall.StageUpdateAsync(install, server, CancellationToken.None); }
        catch (Exception exception) when (exception is HttpRequestException { StatusCode: not null } or InvalidDataException or IOException or
            JsonException or ArgumentException or UnauthorizedAccessException)
        {
            Console.WriteLine($"update: skipped ({exception.Message})");
            return null;
        }
    }

    /// <summary>The staged tool switches itself in once the old one has exited; if the old one is still in use, the next start tries again.</summary>
    private static async Task FinishUpdateAsync(int processId, string tool, string install)
    {
        try
        {
            using var previous = Process.GetProcessById(processId);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await previous.WaitForExitAsync(wait.Token);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OperationCanceledException) { }
        try
        {
            PlayerInstall.InstallTool(tool, install);
            Console.WriteLine("update: the new player tool is installed");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InstallRefusedException)
        { Console.WriteLine($"update: switched at the next start ({exception.Message})"); }
    }

    /// <summary>
    /// The game or AIR folder the player named or the stage remembers, when it passes its check; otherwise the player is asked for it. A
    /// run that cannot ask (redirected input) fails with the reason instead.
    /// </summary>
    private static string Folder(string label, string file, string? given, Func<string, string?> check)
    {
        var folder = given is null ? null : Normalize(given, file);
        while (true)
        {
            var problem = folder is null ? null : check(folder);
            if (folder is not null && problem is null) return folder;
            if (Console.IsInputRedirected)
                throw new ArgumentException(folder is null ? $"No {label} was given; pass it on the command line." : $"The {label} is not usable: {problem}.");
            if (folder is not null) Console.WriteLine($"The {label} is not usable: {problem}.");
            Console.Write($"Drag your {label} here (the folder with {file}), then press Enter: ");
            var answer = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(answer)) throw new OperationCanceledException();
            folder = Normalize(answer, file);
        }
    }

    /// <summary>
    /// The connection the player named; else the server.json beside this program (the operator's hand-out); else the one the stage
    /// remembers; else the player is asked for the file. A token in a file is never kept: the friend's login supplies the bearer.
    /// </summary>
    private static AirRemoteInvitation ConnectionFor(string[] args, string stage)
    {
        if (Program.Option(args, "--connection") is { } named)
        {
            var path = FullPath(named) ?? throw new ArgumentException("The connection file path is not valid.");
            if (!File.Exists(path)) throw new FileNotFoundException("The connection file named on the command line was not found.");
            return Program.ReadRemoteInvitation(path);
        }
        foreach (var candidate in new[] { Path.Combine(AppContext.BaseDirectory, "server.json"), Path.Combine(stage, "connections", "server.json") })
            if (TryConnection(candidate) is { } found) return found;
        if (Console.IsInputRedirected) throw new ArgumentException("No connection file: pass --connection <server.json from the operator>.");
        while (true)
        {
            Console.Write("Drag the connection file (server.json) from the operator here, then press Enter: ");
            var answer = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(answer)) throw new OperationCanceledException();
            if (TryConnection(FullPath(answer)) is { } chosen) return chosen;
            Console.WriteLine("That is not a valid connection file.");
        }
    }

    private static AirRemoteInvitation? TryConnection(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try { return Program.ReadRemoteInvitation(path); }
        catch (Exception exception) when (exception is ArgumentException or JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Copies the player's AIR folder into the stage. AirOverlay reads the source and writes only the copy, which gets the companion's local
    /// endpoints. The copy is built beside the old one and swapped in only when it is complete.
    /// </summary>
    internal static void PrepareAirCopy(string airFolder, string copy, string platformUrl = "http://127.0.0.1:8390", int maestroPort = MaestroPort)
    {
        var next = copy + ".next";
        if (Directory.Exists(next)) Directory.Delete(next, recursive: true);
        Console.WriteLine("Preparing your AIR copy; the first run takes a few minutes. Your AIR folder is only read.");
        try
        {
            AirOverlay.Prepare(new AirOverlayOptions(airFolder, next, platformUrl, maestroPort));
            if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
            Directory.Move(next, copy);
        }
        finally
        {
            if (Directory.Exists(next)) Directory.Delete(next, recursive: true);
        }
    }

    /// <summary>The copy is made again when it is missing or incomplete, or when the stage remembers another AIR folder.</summary>
    internal static bool AirCopyNeeded(string copy, string airFolder, string? rememberedAir) =>
        !Directory.Exists(copy) || AirSourceLayout.Problems(copy).Count > 0 ||
        !string.Equals(rememberedAir, airFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes the stage's two files: the game and AIR folders it runs, and the connection as host, port and certificate pin only (a token
    /// or registration code in the operator's file never reaches the stage).
    /// </summary>
    internal static void WriteStage(string stage, string gameFolder, string airFolder, AirRemoteInvitation connection)
    {
        WriteJson(Path.Combine(stage, "stage.json"), new
        {
            remote = true,
            airSource = Path.GetFullPath(airFolder),
            stockExecutable = Path.Combine(Path.GetFullPath(gameFolder), GameFile),
            maestroPort = MaestroPort,
            clientFiles = "player",
        });
        WriteJson(Path.Combine(stage, "connections", "server.json"), new
        {
            host = connection.Host,
            port = connection.Port,
            certificateSha256 = connection.CertificateSha25664hex,
        });
    }

    internal static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>A string the stage remembers in its stage.json (or another of its files), or null when it has none.</summary>
    private static string? Remembered(string stage, string name, string file = "stage.json")
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, file)));
            return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        { return null; }
    }

    /// <summary>
    /// A folder, or the file named <paramref name="file"/> inside it, as a full path. Drag and drop adds quotes, which are removed.
    /// </summary>
    private static string? Normalize(string text, string file)
    {
        var path = FullPath(text);
        if (path is null) return null;
        return File.Exists(path) && string.Equals(Path.GetFileName(path), file, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(path) : path;
    }

    private static string? FullPath(string text)
    {
        try { return Path.GetFullPath(text.Trim().Trim('"').Trim()); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException) { return null; }
    }
}
