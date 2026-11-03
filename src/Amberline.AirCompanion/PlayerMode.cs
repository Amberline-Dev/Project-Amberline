using System.Diagnostics;
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
        return await StartAsync(install, id!, checkUpdate: finishing is null, ShortcutUi.Windows(status));
    }

    /// <summary>
    /// DIAGNOSTICS: the shortcut's windows and its play step as delegates (a test seam), so a check drives a start, its failures and Try
    /// again without a window and without starting a client. <see cref="Play"/> gets the server folder, its connection file and the
    /// callback that closes the start window once the League client's window shows.
    /// </summary>
    internal sealed record ShortcutUi(Action<string> Status, Action Hide, Func<PlayerProblem, bool> TryAgain, Func<string, string, Action, Task<int>> Play)
    {
        public static ShortcutUi Windows(PlayerInstallWindow.StatusWindow status) => new(status.Show, status.Hide,
            problem =>
            {
                status.Hide();
                return PlayerInstallWindow.AskTryAgain(problem);
            },
            (server, connection, shown) => Program.RunDesktopAsync(server, MaestroPort, connection, shown));
    }

    // Batch 653: each wait says how long it takes.
    internal const string Checking = "Checking for updates. This takes a few seconds.";
    internal const string Starting = "Starting the game client. This takes up to a minute.";

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
        try
        {
            while (true)
            {
                ui.Status(Checking);
                PlayerProblem problem;
                try
                {
                    if (MissingClient(server, title, log) is { } missing) problem = missing;
                    else if (checkUpdate && await StagedUpdateAsync(install, server) is { } staged)
                    {
                        StartStaged(staged, id);
                        return 0;
                    }
                    else
                    {
                        ui.Status(Starting);
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
        if (exception is HttpRequestException { StatusCode: null } or TaskCanceledException or System.Net.Sockets.SocketException)
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
